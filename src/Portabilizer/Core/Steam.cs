using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Portabilizer.Core;

/// <summary>Finds and drives the Steam client through its own command line. Steam still decides who owns what.</summary>
internal static partial class Steam
{
    public static string? Exe
    {
        get
        {
            // tools\smoke.ps1 points this at a fake, so the real Steam is never touched by a test.
            var path = Environment.GetEnvironmentVariable("PORTABILIZER_STEAM_EXE");
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            path ??= key?.GetValue("SteamExe") as string;
            path = path?.Replace('/', '\\');
            return File.Exists(path) ? path : null;
        }
    }

    /// <summary>Every steamapps folder Steam knows about, its own first.</summary>
    public static List<string> Libraries(string exe)
    {
        var main = Path.Combine(Path.GetDirectoryName(exe)!, "steamapps");
        var found = new List<string> { main };
        var folders = Path.Combine(main, "libraryfolders.vdf");
        if (!File.Exists(folders)) return found;
        foreach (Match m in LibraryPath().Matches(File.ReadAllText(folders)))
        {
            var library = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), "steamapps");
            if (Directory.Exists(library) && !found.Contains(library, StringComparer.OrdinalIgnoreCase)) found.Add(library);
        }
        return found;
    }

    public static bool IsRunning(string exe)
    {
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        foreach (var p in running) p.Dispose();
        return running.Length > 0;
    }

    public static void Start(string exe, string argument) => Process.Start(exe, argument)?.Dispose();

    public static void Close(string exe)
    {
        if (!IsRunning(exe)) return;
        Start(exe, "-shutdown");
        for (var i = 0; i < 120 && IsRunning(exe); i++) Thread.Sleep(500);
        if (IsRunning(exe)) throw new IOException("Steam didn't close. Close it yourself, then start this game again.");
    }

    [GeneratedRegex(@"""path""\s+""(.+?)""", RegexOptions.IgnoreCase)]
    private static partial Regex LibraryPath();
}
