using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Microsoft.Win32;
using Portabilizer.Core;

namespace Portabilizer;

public partial class MainWindow : Window
{
    static readonly Brush GoodBrush = Frozen(0x2E, 0x9E, 0x5B);
    static readonly Brush CautionBrush = Frozen(0xD9, 0x95, 0x1A);
    static readonly Brush BlockedBrush = Frozen(0xD1, 0x43, 0x43);

    List<AppRow> _rows = [];
    ICollectionView? _view;
    Level? _show;
    Plan? _plan;

    public MainWindow()
    {
        InitializeComponent();
        Title = $"Portabilizer {System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3)}";
        SaveTo.Text =Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Portable Apps");
        Loaded += async (_, _) =>
        {
            _rows = (await Task.Run(InstalledApps.Scan)).Select(a => new AppRow(a)).ToList();
            _view = CollectionViewSource.GetDefaultView(_rows);
            _view.Filter = Shows;
            Apps.ItemsSource = _view;
            UpdateCounts();
            await CheckAll();
        };
    }

    public static string TextFor(Level verdict) => verdict switch
    {
        Level.Good => "Should work well",
        Level.Caution => "Should work, with limits",
        _ => "Can't be made portable",
    };

    public static Brush BrushFor(Level verdict) => verdict switch
    {
        Level.Good => GoodBrush,
        Level.Caution => CautionBrush,
        _ => BlockedBrush,
    };

    static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    /// <summary>Checks every app in the background so the filters can sort them. A few at a time, since big games take a while to measure.</summary>
    async Task CheckAll()
    {
        await Parallel.ForEachAsync(_rows, new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (row, _) =>
        {
            var plan = await row.GetPlan();
            await Dispatcher.InvokeAsync(() =>
            {
                row.Show(plan.Verdict);
                UpdateCounts();
                if (_show is not null) _view?.Refresh();
            });
        });
    }

    void UpdateCounts()
    {
        int Count(Level level) => _rows.Count(r => r.Verdict == level);
        ShowAll.Content = $"All ({_rows.Count})";
        ShowGood.Content = $"{TextFor(Level.Good)} ({Count(Level.Good)})";
        ShowCaution.Content = $"{TextFor(Level.Caution)} ({Count(Level.Caution)})";
        ShowBlocked.Content = $"{TextFor(Level.Blocked)} ({Count(Level.Blocked)})";
        var left = _rows.Count(r => r.Verdict is null);
        Checking.Text = left > 0 ? $"Checking apps... {_rows.Count - left} of {_rows.Count} done" : "";
        Checking.Visibility = left > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    bool Shows(object item) => item is AppRow row
        && row.Name.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase)
        && (_show is null || row.Verdict == _show);

    void Search_TextChanged(object sender, TextChangedEventArgs e) => _view?.Refresh();

    void Filter_Checked(object sender, RoutedEventArgs e)
    {
        _show = (sender as FrameworkElement)?.Tag as Level?;
        _view?.Refresh();
    }

    async void Apps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Apps.SelectedItem is not AppRow row) return;
        var app = row.App;
        Hint.Visibility = Visibility.Collapsed;
        Details.Visibility = Visibility.Visible;
        AppName.Text = app.Name;
        AppInfo.Text = "Checking...";
        Status.Text = "";
        Make.IsEnabled = false;
        Findings.ItemsSource = null;
        VerdictBox.Visibility = Visibility.Collapsed;
        _plan = null;
        UpdatePreview();

        var plan = await row.GetPlan();
        if (!ReferenceEquals(Apps.SelectedItem, row)) return; // the user has moved on
        _plan = plan;
        row.Show(plan.Verdict);
        AppInfo.Text = string.Join(" · ", new[] { app.Detail, Capturer.Size(plan.AppBytes) }.Where(s => s.Length > 0));
        VerdictText.Text = TextFor(plan.Verdict);
        VerdictBox.Background = new SolidColorBrush(((SolidColorBrush)BrushFor(plan.Verdict)).Color) { Opacity = 0.25 };
        VerdictBox.Visibility = Visibility.Visible;
        Findings.ItemsSource = plan.Findings;
        MainExe.ItemsSource = plan.Exes.Select(x => Path.GetRelativePath(app.InstallPath, x)).ToList();
        MainExe.SelectedIndex = plan.Exes.Count > 0 ? 0 : -1;
        MainExe.IsEnabled = plan.Steam is null; // Steam picks what to start
        IncludeData.IsEnabled = plan.Folders.Count + plan.Keys.Count > 0;
        DataNote.Text = plan.Folders.Count + plan.Keys.Count > 0
            ? $"Adds {Capturer.Size(plan.DataBytes)} of settings. This can include saved sign-ins, so don't share the folder with other people if this is ticked."
            : "No settings were found for this app.";
        Make.IsEnabled = plan.Verdict != Level.Blocked;
        UpdatePreview();
    }

    static string Bare(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();

    /// <summary>Where the package goes: its own folder inside the chosen one, unless the user turns that off.</summary>
    string? Target()
    {
        var chosen = SaveTo.Text.Trim();
        if (chosen.Length == 0) return null;
        if (OwnFolder.IsChecked != true) return chosen;
        return Apps.SelectedItem is AppRow row ? Path.Combine(chosen, $"{Bare(row.Name)} Portable") : null;
    }

    void SaveTo_Changed(object sender, RoutedEventArgs e) => UpdatePreview();

    void UpdatePreview()
    {
        if (SavePreview is null) return; // events fire while the window is still being built
        SavePreview.Text = Target() is { } target
            ? $"Will be saved in {target}" + (OwnFolder.IsChecked == true ? "" : ", alongside anything already there")
            : "";
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose where to save the portable app" };
        if (Directory.Exists(SaveTo.Text.Trim())) dialog.InitialDirectory = SaveTo.Text.Trim();
        if (dialog.ShowDialog(this) == true) SaveTo.Text = dialog.FolderName;
    }

    async void Make_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is not { } plan || MainExe.SelectedIndex < 0 || Target() is not { } folder) return;
        var mainExe = plan.Exes[MainExe.SelectedIndex];
        var include = IncludeData.IsChecked == true;

        IsEnabled = false;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        var progress = new Progress<(double Fraction, string Status)>(p =>
        {
            Progress.Value = p.Fraction;
            Status.Text = p.Status;
        });
        try
        {
            await Task.Run(() => Capturer.Run(plan, mainExe, include, folder, progress));
            Status.Text = $"Done. Copy \"{folder}\" anywhere and run the program inside it.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status.Text = "";
            MessageBox.Show(this, ex.Message, "Portabilizer", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Progress.Visibility = Visibility.Collapsed;
            IsEnabled = true;
        }
    }
}
