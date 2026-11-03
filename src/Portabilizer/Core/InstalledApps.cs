using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Portabilizer.Core;

public sealed record InstalledApp(string Name, string Version, string Publisher, string InstallPath, string IconExe)
{
    public string Detail => string.Join(" · ", new[] { Version, Publisher }.Where(s => s.Length > 0));
}

/// <summary>Reads the same list Windows shows under Installed apps. Store apps aren't in it.</summary>
public static partial class InstalledApps
{
    const string Uninstall = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public static List<InstalledApp> Scan()
    {
        var found = Read(Registry.LocalMachine, Uninstall)
            .Concat(Read(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"))
            .Concat(Read(Registry.CurrentUser, Uninstall));
        return found.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(a => a.InstallPath.Length > 0).First())
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    static IEnumerable<InstalledApp> Read(RegistryKey hive, string path)
    {
        using var root = hive.OpenSubKey(path);
        if (root is null) yield break;
        foreach (var id in root.GetSubKeyNames())
        {
            using var k = root.OpenSubKey(id);
            if (k?.GetValue("DisplayName") is not string name || string.IsNullOrWhiteSpace(name)) continue;
            if (k.GetValue("SystemComponent") is 1 || k.GetValue("ParentKeyName") is not null || NotAnApp().IsMatch(name)) continue;
            var icon = ExeIn(k.GetValue("DisplayIcon") as string);
            var uninstall = k.GetValue("UninstallString") as string;
            var uninstaller = uninstall?.Contains("steam://") == true ? "" : ExeIn(uninstall); // Steam's entry names Steam itself, not the game
            var install = (k.GetValue("InstallLocation") as string)?.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\') ?? "";
            if (install.Length == 0) install = Path.GetDirectoryName(icon.Length > 0 ? icon : uninstaller) ?? "";
            yield return new InstalledApp(name.Trim(), k.GetValue("DisplayVersion") as string ?? "", k.GetValue("Publisher") as string ?? "", install, icon);
        }
    }

    /// <summary>Pulls an exe path out of an icon or uninstall value, ignoring anything under Windows such as msiexec.</summary>
    static string ExeIn(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var m = ExePath().Match(value);
        if (!m.Success) return "";
        var path = m.Groups[1].Value;
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        return path.StartsWith(windows, StringComparison.OrdinalIgnoreCase) ? "" : path;
    }

    [GeneratedRegex(@"^(Update for|Security Update|Hotfix|Microsoft Visual C\+\+|Microsoft \.NET|Microsoft ASP\.NET|Microsoft Windows Desktop Runtime|Windows SDK|Windows Software Development Kit)", RegexOptions.IgnoreCase)]
    private static partial Regex NotAnApp();

    [GeneratedRegex(@"([A-Za-z]:\\[^""]*?\.exe)", RegexOptions.IgnoreCase)]
    private static partial Regex ExePath();
}
