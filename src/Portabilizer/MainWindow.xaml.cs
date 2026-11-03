using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using Portabilizer.Core;

namespace Portabilizer;

public partial class MainWindow : Window
{
    List<InstalledApp> _all = [];
    Plan? _plan;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            _all = await Task.Run(InstalledApps.Scan);
            Apps.ItemsSource = _all;
        };
    }

    void Search_TextChanged(object sender, TextChangedEventArgs e) =>
        Apps.ItemsSource = _all.Where(a => a.Name.Contains(Search.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();

    async void Apps_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Apps.SelectedItem is not InstalledApp app) return;
        Hint.Visibility = Visibility.Collapsed;
        Details.Visibility = Visibility.Visible;
        AppName.Text = app.Name;
        AppInfo.Text = "Checking...";
        Status.Text = "";
        Make.IsEnabled = false;
        Findings.ItemsSource = null;
        VerdictBox.Visibility = Visibility.Collapsed;

        var plan = await Task.Run(() => Inspector.Inspect(app));
        if (!ReferenceEquals(Apps.SelectedItem, app)) return; // the user has moved on
        _plan = plan;
        AppInfo.Text = string.Join(" · ", new[] { app.Detail, Capturer.Size(plan.AppBytes) }.Where(s => s.Length > 0));
        Show(plan.Verdict);
        Findings.ItemsSource = plan.Findings;
        MainExe.ItemsSource = plan.Exes.Select(x => Path.GetRelativePath(app.InstallPath, x)).ToList();
        MainExe.SelectedIndex = plan.Exes.Count > 0 ? 0 : -1;
        MainExe.IsEnabled = plan.Steam is null; // Steam picks what to start
        IncludeData.IsEnabled = plan.Folders.Count + plan.Keys.Count > 0;
        DataNote.Text = plan.Folders.Count + plan.Keys.Count > 0
            ? $"Adds {Capturer.Size(plan.DataBytes)} of settings. This can include saved sign-ins, so don't share the folder with other people if this is ticked."
            : "No settings were found for this app.";
        SaveTo.Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Portable Apps", $"{Bare(app.Name)} Portable");
        Make.IsEnabled = plan.Verdict != Level.Blocked;
    }

    static string Bare(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();

    void Show(Level verdict)
    {
        var (text, color) = verdict switch
        {
            Level.Good => ("Should work well", Color.FromRgb(0x2E, 0x9E, 0x5B)),
            Level.Caution => ("Should work, with limits", Color.FromRgb(0xD9, 0x95, 0x1A)),
            _ => ("Can't be made portable", Color.FromRgb(0xD1, 0x43, 0x43)),
        };
        VerdictText.Text = text;
        VerdictBox.Background = new SolidColorBrush(color) { Opacity = 0.25 };
        VerdictBox.Visibility = Visibility.Visible;
    }

    void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Choose an empty folder for the portable app" };
        if (dialog.ShowDialog(this) == true) SaveTo.Text = dialog.FolderName;
    }

    async void Make_Click(object sender, RoutedEventArgs e)
    {
        if (_plan is not { } plan || MainExe.SelectedIndex < 0 || string.IsNullOrWhiteSpace(SaveTo.Text)) return;
        var mainExe = plan.Exes[MainExe.SelectedIndex];
        var include = IncludeData.IsChecked == true;
        var folder = SaveTo.Text.Trim();

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
