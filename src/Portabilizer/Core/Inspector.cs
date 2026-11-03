using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Portabilizer.Core;

public enum Level { Good, Caution, Blocked }

public sealed record Finding(Level Level, string Text);
public sealed record DataFolder(string RealPath, string Target, long Bytes);
public sealed record SteamGame(string AppId, string InstallDir, string ManifestPath);

/// <summary>What capturing an app would involve, and how likely it is to work elsewhere.</summary>
public sealed class Plan
{
    public required InstalledApp App { get; init; }
    public List<string> Exes { get; } = [];
    public long AppBytes { get; set; }
    public SteamGame? Steam { get; set; }
    internal List<DotNet.Need> DotNetNeeds { get; } = [];
    public long RuntimeBytes { get; set; }
    public List<DataFolder> Folders { get; } = [];
    public List<string> Keys { get; } = [];
    public List<Finding> Findings { get; } = [];
    public Level Verdict => Findings.Count == 0 ? Level.Good : Findings.Max(f => f.Level);
    public long DataBytes => Folders.Sum(f => f.Bytes);
}

/// <summary>Works out what belongs to an app, reading only. Nothing on the PC is changed here.</summary>
public static partial class Inspector
{
    static readonly string[] Denied = ["microsoft", "windows", "temp", "packages", "programs", "classes", "policies", "clients", "common files", "wow6432node", "program files",
        "application", "app", "apps", "bin", "program", "current", "software", "common", "steamapps"];
    static readonly string[] AntiCheat = ["EasyAntiCheat", "BattlEye", "BEService", "Vanguard", "xigncode", "nProtect"];

    public static Plan Inspect(InstalledApp app)
    {
        var plan = new Plan { App = app };
        if (app.InstallPath.Length == 0 || !Directory.Exists(app.InstallPath))
        {
            plan.Findings.Add(new(Level.Blocked, "This app's folder can't be found, so there's nothing to copy."));
            return plan;
        }
        if (app.InstallPath.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase))
        {
            plan.Findings.Add(new(Level.Blocked, "This is part of Windows, so it can't be moved."));
            return plan;
        }

        var names = NamesFor(app);
        var runtimeConfigs = ReadInstallFolder(plan);
        FindData(plan, names);
        FindKeys(plan, names);
        CheckServices(plan, ProductOf(app));
        CheckKnownNeeds(plan);
        CheckDotNet(plan, runtimeConfigs);

        if (plan.Exes.Count == 0) plan.Findings.Add(new(Level.Blocked, "No program file was found in the install folder."));
        if (plan.Keys.Any(k => k.StartsWith("HKLM"))) plan.Findings.Add(new(Level.Caution, "Will ask for administrator permission each time it starts, because it keeps settings in a PC-wide part of Windows."));
        if (plan.Verdict == Level.Good) plan.Findings.Add(new(Level.Good, "Nothing here should stop it working from a portable folder."));
        if (plan.Folders.Count > 0) plan.Findings.Add(new(Level.Good, "Saved sign-ins usually can't be read on another PC, so you may need to sign in again."));
        return plan;
    }

    static string ProductOf(InstalledApp app) => TrailingDetail().Replace(Brackets().Replace(app.Name, ""), "").Trim();

    static List<string> NamesFor(InstalledApp app)
    {
        var product = ProductOf(app);
        var publisher = CompanySuffix().Replace(app.Publisher, "").Trim().TrimEnd(',', '.').Trim();
        var folder = Path.GetFileName(app.InstallPath);
        var underPrograms = UnderPrograms(app.InstallPath);
        return new[] { $@"{publisher}\{product}", $@"{publisher}\{folder}", underPrograms, product, folder }
            .Where(n => n.Length > 0 && n.Split('\\').All(Usable))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>Vendors reuse their folder layout for settings: Program Files\Acme\Tool\Application means Acme\Tool.</summary>
    static string UnderPrograms(string install)
    {
        Environment.SpecialFolder[] roots = [Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.LocalApplicationData];
        foreach (var root in roots.Select(Environment.GetFolderPath))
        {
            if (!Inside(install, root)) continue;
            var parts = Path.GetRelativePath(root, install).Split('\\');
            return string.Join('\\', parts.Length > 1 && Denied.Contains(parts[^1], StringComparer.OrdinalIgnoreCase) ? parts[..^1] : parts);
        }
        return "";
    }

    static bool Usable(string part) => part.Length >= 3
        && part.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && !Denied.Contains(part, StringComparer.OrdinalIgnoreCase);

    static List<string> ReadInstallFolder(Plan plan)
    {
        var runtimeConfigs = new List<string>();
        var exes = new List<(string Path, long Size)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (var file in new DirectoryInfo(plan.App.InstallPath).EnumerateFiles("*", options))
        {
            plan.AppBytes += file.Length;
            if (file.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase)) exes.Add((file.FullName, file.Length));
            seen.Add(file.Name);
            foreach (var part in file.FullName.Split('\\')) seen.Add(part);
            if (file.Name.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase)) runtimeConfigs.Add(file.FullName);
        }

        foreach (var name in AntiCheat)
            if (seen.Any(s => s.Contains(name, StringComparison.OrdinalIgnoreCase)))
                AddOnce(plan, new(Level.Blocked, $"Uses anti-cheat ({name}), which needs a driver installed on each PC and may treat a moved game as tampering."));

        var icon = plan.App.IconExe;
        var wanted = new[] { Letters(plan.App.Name), Letters(Path.GetFileName(plan.App.InstallPath)) };
        plan.Exes.AddRange(exes
            .OrderBy(e => NotMainExe().IsMatch(Path.GetRelativePath(plan.App.InstallPath, e.Path)) ? 1 : 0)
            .ThenBy(e => e.Path.Equals(icon, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(e => NameMatches(Path.GetFileNameWithoutExtension(e.Path), wanted) ? 0 : 1)
            .ThenBy(e => e.Path.Count(c => c == '\\'))
            .ThenByDescending(e => e.Size)
            .Select(e => e.Path));
        return runtimeConfigs;
    }

    /// <summary>
    /// Apps built on shared .NET get their own copy, from Portabilizer's library, a download, or this PC, as the library
    /// settings say. Nothing is downloaded here; that waits until the app is made portable. Steam starts its own games, so
    /// it can't be handed one.
    /// </summary>
    static void CheckDotNet(Plan plan, List<string> runtimeConfigs)
    {
        var mode = Library.Settings.DotNet;
        var ready = new List<DotNet.Framework>();
        foreach (var config in runtimeConfigs)
        {
            var frameworks = DotNet.Needs(config);
            if (frameworks.Count == 0) continue;
            var exe = config[..^".runtimeconfig.json".Length] + ".exe";
            var x86 = File.Exists(exe) && Runtimes.MachineOf(exe) == System.Reflection.PortableExecutable.Machine.I386;
            var need = new DotNet.Need(frameworks, x86, $".NET {frameworks[0].Version.ToString(2)}");
            if (plan.Steam is not null)
            {
                AddOnce(plan, new(Level.Caution, $"Needs {need.Label} installed on each PC."));
                continue;
            }
            var inLibrary = mode == DotNetSource.ThisPcOnly ? null : DotNet.Resolve(need, [Library.DotNetRoot(x86)]);
            var onPc = DotNet.Resolve(need, [DotNet.PcRoot(x86)]);
            var from = inLibrary is not null ? "from Portabilizer's library"
                : mode == DotNetSource.LibraryThenDownload ? "downloaded from Microsoft when you make it portable"
                : onPc is not null ? "from this PC"
                : null;
            if (from is null)
            {
                AddOnce(plan, new(Level.Caution, $"Needs {need.Label} installed on each PC. Neither Portabilizer's library nor this PC has it, so it can't be included."));
                continue;
            }
            plan.DotNetNeeds.Add(need);
            ready.AddRange(inLibrary ?? onPc ?? []);
            AddOnce(plan, new(Level.Good, $"Brings its own copy of {need.Label}, {from}, so the other PC doesn't need it installed."));
        }
        plan.RuntimeBytes = DotNet.Folders(ready.Distinct()).Sum(f => File.Exists(f.From) ? new FileInfo(f.From).Length : SizeOf(f.From));
    }

    static string Letters(string text) => string.Concat(text.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    static bool NameMatches(string exeStem, string[] wanted)
    {
        var stem = Letters(exeStem);
        return stem.Length >= 3 && wanted.Any(w => w.Contains(stem) || stem.Contains(w));
    }

    static void AddOnce(Plan plan, Finding f)
    {
        if (!plan.Findings.Contains(f)) plan.Findings.Add(f);
    }

    static void FindData(Plan plan, List<string> names)
    {
        (string Real, string Token)[] roots =
        [
            (Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "%PROGRAMDATA%"),
            (Path.Combine(Win.Profile, "AppData", "LocalLow"), @"%USERPROFILE%\AppData\LocalLow"),
            (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "My Games"), @"%DOCUMENTS%\My Games"),
        ];
        foreach (var (real, token) in roots)
            foreach (var name in names)
            {
                var path = Path.Combine(real, name);
                if (!Directory.Exists(path) || Win.IsLink(path)) continue;
                // An app that installs itself under AppData is already covered by the App folder.
                if (Inside(plan.App.InstallPath, path) || Inside(path, plan.App.InstallPath)) continue;
                if (plan.Folders.Any(f => Inside(path, f.RealPath))) continue;
                plan.Folders.Add(new(path, $@"{token}\{name}", SizeOf(path)));
            }
    }

    static bool Inside(string path, string folder) =>
        (path.TrimEnd('\\') + @"\").StartsWith(folder.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase);

    static long SizeOf(string path)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return new DirectoryInfo(path).EnumerateFiles("*", options).Sum(f => f.Length);
    }

    static void FindKeys(Plan plan, List<string> names)
    {
        string[] roots = [@"HKCU\Software", @"HKLM\SOFTWARE", @"HKLM\SOFTWARE\WOW6432Node"];
        foreach (var root in roots)
            foreach (var name in names)
            {
                var key = $@"{root}\{name}";
                if (!Win.KeyExists(key) || plan.Keys.Any(k => (key + @"\").StartsWith(k + @"\", StringComparison.OrdinalIgnoreCase))) continue;
                plan.Keys.Add(key);
            }
    }

    static void CheckServices(Plan plan, string product)
    {
        using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
        if (services is null) return;
        var install = plan.App.InstallPath.TrimEnd('\\') + @"\";
        foreach (var id in services.GetSubKeyNames())
        {
            using var s = services.OpenSubKey(id);
            if (s?.GetValue("Type") is not int type) continue;
            var image = s.GetValue("ImagePath", "", RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? "";
            var display = s.GetValue("DisplayName") as string ?? "";
            var named = product.Length >= 5 && Usable(product) && (id.Contains(product, StringComparison.OrdinalIgnoreCase) || display.Contains(product, StringComparison.OrdinalIgnoreCase));
            if (!named && !image.Contains(install, StringComparison.OrdinalIgnoreCase)) continue;
            var label = display.Length > 0 && !display.StartsWith('@') ? display : id;
            plan.Findings.Add((type & 0x3) != 0
                ? new(Level.Blocked, $"Uses a driver ({label}). Drivers must be installed on each PC, so this app can't run from a folder.")
                : new(Level.Caution, $"Uses a background Windows service ({label}), which won't run on other PCs. Anything relying on it, often updates, won't work."));
        }
    }

    static void CheckKnownNeeds(Plan plan)
    {
        var path = plan.App.InstallPath;
        if (path.Contains(@"\steamapps\", StringComparison.OrdinalIgnoreCase))
        {
            plan.Steam = FindSteamGame(path);
            if (plan.Steam is null)
                plan.Findings.Add(new(Level.Blocked, "Steam hasn't finished installing or updating this game, so it can't be copied yet."));
            else
            {
                plan.Findings.Add(new(Level.Caution, "Steam must be installed on each PC and signed in to the account that owns this game. Run the portable file once on each PC, and Steam restarts once. After that you can just press Play in Steam, with no download."));
                plan.Findings.Add(new(Level.Caution, "The game stays connected to that PC's Steam until you run Disconnect from this PC. Do that before deleting the portable folder, and don't choose Uninstall in Steam while connected, as that could delete the portable copy."));
            }
        }
        if (path.Contains(@"\Epic Games\", StringComparison.OrdinalIgnoreCase))
            plan.Findings.Add(new(Level.Caution, "This is an Epic game, so the Epic launcher must be installed and signed in on each PC."));
        if (plan.App.Publisher.Contains("Riot Games", StringComparison.OrdinalIgnoreCase))
            plan.Findings.Add(new(Level.Blocked, "Riot's games need its Vanguard anti-cheat driver installed on each PC."));
        if (plan.App.Publisher.Contains("Adobe", StringComparison.OrdinalIgnoreCase))
            plan.Findings.Add(new(Level.Caution, "Adobe apps depend on Adobe's own sign-in and background services, so this may refuse to start elsewhere."));
    }

    /// <summary>Finds Steam's record of a fully installed game, which is what lets another PC's Steam accept the same files.</summary>
    static SteamGame? FindSteamGame(string install)
    {
        const string Common = @"\steamapps\common\";
        var at = install.IndexOf(Common, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return null;
        var steamapps = install[..(at + Common.Length - @"common\".Length)];
        var folder = install[(at + Common.Length)..].Split('\\')[0];
        foreach (var record in Directory.EnumerateFiles(steamapps, "appmanifest_*.acf"))
        {
            var text = File.ReadAllText(record);
            if (!Steam.InstallDirOf(text).Equals(folder, StringComparison.OrdinalIgnoreCase)) continue;
            if (!FullyInstalledLine().IsMatch(text)) return null;
            return new(Path.GetFileNameWithoutExtension(record)["appmanifest_".Length..], folder, record);
        }
        return null;
    }

    [GeneratedRegex(@"""StateFlags""\s+""4""", RegexOptions.IgnoreCase)]
    private static partial Regex FullyInstalledLine();

    [GeneratedRegex(@"\(.*?\)")]
    private static partial Regex Brackets();

    [GeneratedRegex(@"\s+(v?\d[\d.]*\b.*|x64|x86|64-bit|32-bit)$", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingDetail();

    [GeneratedRegex(@"[,\s]+(inc|llc|ltd|limited|corp|corporation|gmbh|co|company|s\.?r\.?o|ag|b\.?v)\.?$", RegexOptions.IgnoreCase)]
    private static partial Regex CompanySuffix();

    [GeneratedRegex(@"unins|setup|install|update|crash|report|helper|redist|dxsetup|vc_|dotnet", RegexOptions.IgnoreCase)]
    private static partial Regex NotMainExe();
}
