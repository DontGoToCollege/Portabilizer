using System.Windows;
using System.Windows.Controls;
using Portabilizer.Core;

namespace Portabilizer;

public partial class LibraryWindow : Window
{
    sealed record ChannelRow(string Version, string Label, string Status);

    static readonly string[] AllFrameworks = [DotNet.BaseName, "Microsoft.WindowsDesktop.App", "Microsoft.AspNetCore.App"];
    List<DotNetChannel> _channels = [];
    bool _ready;

    /// <summary>True once anything here could change how apps are judged, so the main list rechecks them.</summary>
    public bool Changed { get; private set; }

    public LibraryWindow()
    {
        InitializeComponent();
        var settings = Library.Settings;
        (settings.DotNet switch
        {
            DotNetSource.LibraryThenDownload => NetDownload,
            DotNetSource.LibraryThenThisPc => NetLibraryPc,
            _ => NetPc,
        }).IsChecked = true;
        (settings.Files == FilesSource.ThisPcOnly ? FilesPc : FilesLibrary).IsChecked = true;
        Include32.IsChecked = settings.Include32Bit;
        _ready = true;
        Refresh();
        Loaded += async (_, _) =>
        {
            Status.Text = "Asking Microsoft which .NET versions there are...";
            try
            {
                _channels = await Library.Channels();
                Status.Text = "";
            }
            catch (IOException e)
            {
                Offline.Text = $"Downloads aren't available right now. {e.Message}";
                Offline.Visibility = Visibility.Visible;
                Status.Text = "";
            }
            Refresh();
        };
    }

    void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready) return; // the choices are being filled in from the saved settings
        var settings = Library.Settings;
        settings.DotNet = NetDownload.IsChecked == true ? DotNetSource.LibraryThenDownload
            : NetLibraryPc.IsChecked == true ? DotNetSource.LibraryThenThisPc
            : DotNetSource.ThisPcOnly;
        settings.Files = FilesPc.IsChecked == true ? FilesSource.ThisPcOnly : FilesSource.LibraryThenThisPc;
        settings.Include32Bit = Include32.IsChecked == true;
        try { Library.SaveSettings(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status.Text = $"Couldn't save the settings: {ex.Message}"; }
        Changed = true;
    }

    void Refresh()
    {
        Location.Text = $"Kept in {Library.Root} ({Capturer.Size(Library.Size())})";
        var x64 = Library.DotNetVersions(false);
        var x86 = Library.DotNetVersions(true);
        Channels.ItemsSource = _channels.Select(c =>
        {
            var have = x64.Where(v => v.StartsWith(c.Version + ".")).ToList();
            var have32 = x86.Where(v => v.StartsWith(c.Version + ".")).ToList();
            var status = have.Count + have32.Count == 0 ? "Not in the library"
                : "In the library: " + string.Join(", ", have) + (have32.Count > 0 ? $" (32-bit: {string.Join(", ", have32)})" : "");
            var label = $".NET {c.Version}" + (c.Phase == "eol" ? "  ·  no longer updated by Microsoft" : "");
            return new ChannelRow(c.Version, label, status);
        }).ToList();
        FilesStatus.Text = $"{Library.FileCount()} Visual C++ and DirectX files in the library.";
    }

    async Task Run(Func<Action<string>, Task<string>> work)
    {
        Body.IsEnabled = false;
        Busy.Visibility = Visibility.Visible;
        void Report(string text) => Dispatcher.Invoke(() => Status.Text = text);
        try { Status.Text = await Task.Run(() => work(Report)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Status.Text = e.Message; }
        finally
        {
            Busy.Visibility = Visibility.Collapsed;
            Body.IsEnabled = true;
            Changed = true;
            Refresh();
        }
    }

    async Task<string> DownloadChannel(string version, bool also32, Action<string> report)
    {
        var skipped = await Library.Download(version, AllFrameworks, false, report, required: false);
        if (also32) skipped.AddRange(await Library.Download(version, AllFrameworks, true, report, required: false));
        return skipped.Count == 0
            ? $".NET {version} is in the library."
            : $".NET {version} is in the library, except the part for {string.Join(" and ", skipped.Distinct())}, which Microsoft only offers as an installer.";
    }

    async void Download_Click(object sender, RoutedEventArgs e)
    {
        var version = (string)((Button)sender).Tag;
        var also32 = Include32.IsChecked == true;
        await Run(report => DownloadChannel(version, also32, report));
    }

    async void DownloadAll_Click(object sender, RoutedEventArgs e)
    {
        var versions = _channels.Select(c => c.Version).ToList();
        if (versions.Count == 0)
        {
            Status.Text = "Microsoft's list of .NET versions couldn't be reached, so there's nothing to download right now.";
            return;
        }
        var also32 = Include32.IsChecked == true;
        await Run(async report =>
        {
            var notes = new List<string>();
            foreach (var version in versions)
                if (await DownloadChannel(version, also32, report) is { } note && note.Contains("except")) notes.Add(note);
            return notes.Count == 0 ? "Every .NET version is in the library." : "Every .NET version is in the library. " + string.Join(" ", notes);
        });
    }

    async void AddPcDotNet_Click(object sender, RoutedEventArgs e) => await Run(report =>
    {
        Library.AddThisPcDotNet(report);
        return Task.FromResult("This PC's .NET versions are in the library.");
    });

    async void AddPcFiles_Click(object sender, RoutedEventArgs e) => await Run(_ =>
    {
        var added = Library.AddThisPcFiles();
        return Task.FromResult(added == 0 ? "The library already had all of this PC's files." : $"Added {added} files from this PC.");
    });
}
