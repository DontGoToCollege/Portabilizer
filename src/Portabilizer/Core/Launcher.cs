using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace Portabilizer.Core;

/// <summary>
/// Runs a package: sets the app's settings aside for this PC's own copy, links in the portable ones, starts the app,
/// then puts everything back. Every step is journalled on this PC first, so a crash or power cut is undone on the next launch.
/// </summary>
public static class Launcher
{
    const string Aside = ".portabilizer-aside";

    sealed class Session
    {
        public string Root { get; set; } = "";
        public List<int> Started { get; set; } = [];  // registry keys set aside
        public List<int> Imported { get; set; } = []; // registry keys holding the app's data right now
        public List<string> Links { get; set; } = [];
        public List<string> CreatedDirs { get; set; } = [];
        public bool Complete { get; set; }            // a Steam game's settings are fully connected
    }

    /// <summary>A game connected to this PC's Steam. It outlives each session, so Steam only has to restart once per PC.</summary>
    sealed class SteamConnection
    {
        public string Record { get; set; } = "";
        public string GameDir { get; set; } = "";
        public bool RecordPlaced { get; set; }
    }

    public static int Run(string root, string[] args)
    {
        try
        {
            var m = Manifest.Load(root);
            if (m.NeedsAdmin && !Win.IsAdmin) return Elevate(m, args);

            using var mutex = new Mutex(true, $@"Local\Portabilizer-{m.Id}", out var first);
            if (!first)
            {
                Show($"{m.Name} is already open.");
                return 1;
            }

            // Kept on this PC, not in the package: what needs undoing belongs to this PC, and its backups shouldn't travel.
            var state = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Portabilizer", m.Id);
            Directory.CreateDirectory(state);

            if (m.SteamAppId is not null)
            {
                if (args.Contains("--disconnect")) DisconnectFromSteam(m, root, state);
                else PlayThroughSteam(m, root, state);
                return 0;
            }

            // Swapping an app's settings while any copy of it is open scrambles them, so nothing moves until it's closed.
            if (AppIsOpen(m, root))
            {
                Show($"{m.Name} is open on this PC. Close it, including from the system tray, then start this again.");
                return 1;
            }

            if (LoadSession(state) is { } unfinished) Restore(m, root, state, unfinished);

            var session = new Session { Root = root };
            try
            {
                Save(state, session);
                TakeOver(m, root, state, session);
                Launch(m, root, args);
            }
            finally
            {
                Restore(m, root, state, session);
            }
            return 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or JsonException or InvalidDataException)
        {
            Show($"Something went wrong: {e.Message}\n\nNothing has been lost. Starting this app again will finish tidying up.");
            return 1;
        }
    }

    static void TakeOver(Manifest m, string root, string state, Session s)
    {
        var appDir = Path.Combine(root, "App");
        foreach (var folder in m.Folders)
            LinkFolder(Win.Expand(folder.Target), Path.Combine(root, folder.Source), state, s);

        // Best effort: covers apps with their install path written into their own files. Program Files needs admin rights.
        if (m.SteamAppId is null && !Directory.Exists(m.InstallPath))
            try
            {
                MakeParents(m.InstallPath, s);
                s.Links.Add(m.InstallPath);
                Save(state, s);
                Win.CreateJunction(m.InstallPath, appDir);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                s.Links.Remove(m.InstallPath);
                Save(state, s);
            }

        for (var i = 0; i < m.Registry.Count; i++)
        {
            var entry = m.Registry[i];
            if (Win.KeyExists(entry.Key)) Win.Reg("export", entry.Key, Backup(state, i), "/y");
            s.Started.Add(i);
            Save(state, s);
            if (Win.KeyExists(entry.Key)) Win.Reg("delete", entry.Key, "/f");

            var live = Path.Combine(state, $"{i}.live.reg");
            File.WriteAllText(live, Win.FromTokens(File.ReadAllText(Path.Combine(root, entry.File), Encoding.Unicode), appDir, Win.Profile), Encoding.Unicode);
            Win.Reg("import", live);
            File.Delete(live);
            s.Imported.Add(i);
            Save(state, s);
        }
    }

    static void LinkFolder(string target, string source, string state, Session s)
    {
        if (Directory.Exists(target))
        {
            if (Directory.Exists(target + Aside)) throw new IOException($"{target + Aside} is in the way. Move it somewhere else and try again.");
            Directory.Move(target, target + Aside);
        }
        MakeParents(target, s);
        s.Links.Add(target);
        Save(state, s);
        Win.CreateJunction(target, source);
    }

    static void MakeParents(string path, Session s)
    {
        var missing = new Stack<string>();
        for (var dir = Path.GetDirectoryName(path); dir is not null && !Directory.Exists(dir); dir = Path.GetDirectoryName(dir)) missing.Push(dir);
        foreach (var dir in missing)
        {
            Directory.CreateDirectory(dir);
            s.CreatedDirs.Add(dir);
        }
    }

    static void Launch(Manifest m, string root, string[] args)
    {
        var exe = Path.Combine(root, m.MainExe);
        var psi = new ProcessStartInfo(exe) { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        // An app built on .NET looks here before the PC's own .NET, so the copy in the package is used.
        (string Arch, string[] Names)[] roots = [("x64", ["DOTNET_ROOT", "DOTNET_ROOT_X64"]), ("x86", ["DOTNET_ROOT(x86)", "DOTNET_ROOT_X86"])];
        foreach (var (arch, names) in roots)
        {
            var dir = Path.Combine(root, "Data", "DotNet", arch);
            if (Directory.Exists(dir)) foreach (var name in names) psi.Environment[name] = dir;
        }

        Process? main;
        const int ElevationRequired = 740;
        try { main = Process.Start(psi); }
        catch (Win32Exception e) when (e.NativeErrorCode == ElevationRequired)
        {
            main = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = psi.WorkingDirectory, UseShellExecute = true, Verb = "runas", Arguments = Quote(args) });
        }
        main?.WaitForExit();
        main?.Dispose();

        // Many apps hand over to another of their own programs and exit, so a quiet spell has to last before the app counts as closed.
        for (var quiet = 0; quiet < 5;)
        {
            quiet = AppIsOpen(m, root) ? 0 : quiet + 1;
            Thread.Sleep(1500);
        }
    }

    /// <summary>Apps can hand over to a copy of themselves installed on the PC, so the portable folder alone isn't enough to watch.</summary>
    static bool AppIsOpen(Manifest m, string root) =>
        Win.AnyRunningFrom(Path.Combine(root, "App")) || Win.AnyRunningFrom(m.InstallPath) || Win.AnyRunningNamed(Path.GetFileName(m.MainExe));

    /// <summary>
    /// Gives Steam the same two things a normal install has, its record of the game and the game's folder, with the folder
    /// linked to the portable files. Steam only reads records when it starts, so connecting needs one restart. After that
    /// Steam treats the game as installed, so Steam's own Play button works too, and nothing is undone until it's disconnected.
    /// </summary>
    static void PlayThroughSteam(Manifest m, string root, string state)
    {
        var exe = Steam.Exe ?? throw new IOException("This game needs Steam, and Steam isn't installed on this PC.");
        var appDir = Path.Combine(root, "App");
        var name = $"appmanifest_{m.SteamAppId}.acf";
        var libraries = Steam.Libraries(exe);
        var library = libraries.FirstOrDefault(l => File.Exists(Path.Combine(l, name))) ?? libraries[0];
        var record = Path.Combine(library, name);
        var gameDir = Path.Combine(library, "common", m.SteamInstallDir!);
        var connection = LoadConnection(state);

        if (File.Exists(record) && Directory.Exists(gameDir) && !Win.IsLink(gameDir))
        {
            // A PC with its own copy just plays that one with its own saves, and none of Steam's files are touched.
            File.Delete(ConnectionFile(state));
            if (LoadSession(state) is { } settings) Restore(m, root, state, settings);
        }
        else
        {
            if (File.Exists(record) && Win.IsLink(gameDir))
            {
                // Already connected. The drive letter may have changed, or this may be a newer package of the same game.
                if (!SameFolder(new DirectoryInfo(gameDir).LinkTarget, appDir))
                {
                    Directory.Delete(gameDir);
                    Win.CreateJunction(gameDir, appDir);
                }
                SaveConnection(state, connection ?? new SteamConnection { Record = record, GameDir = gameDir, RecordPlaced = true });
                // Steam may have updated the game, and its record says which version the files now are. This keeps the next PC in step.
                File.Copy(record, Path.Combine(root, "Data", "Steam", name), overwrite: true);
            }
            else
            {
                if (Steam.IsRunning(exe))
                {
                    if (!AskToRestartSteam($"Steam needs to restart once so it can find {m.Name}. Restart Steam now?")) return;
                    Steam.Close(exe);
                }
                if (connection is not null) Release(root, state, connection); // Steam dropped the game, so start clean
                Connect(name, root, state, record, gameDir);
            }
            ConnectSettings(m, root, state);
        }

        Steam.Start(exe, $"steam://rungameid/{m.SteamAppId}");
    }

    /// <summary>Saves stay connected along with the game, so Steam's Play button uses the portable ones as well.</summary>
    static void ConnectSettings(Manifest m, string root, string state)
    {
        var session = LoadSession(state);
        if (session is { Complete: true })
        {
            foreach (var folder in m.Folders)
            {
                var target = Win.Expand(folder.Target);
                var source = Path.Combine(root, folder.Source);
                if (!Win.IsLink(target) || SameFolder(new DirectoryInfo(target).LinkTarget, source)) continue;
                Directory.Delete(target);
                Win.CreateJunction(target, source);
            }
            return;
        }

        if (session is not null) Restore(m, root, state, session); // an earlier attempt was interrupted
        session = new Session { Root = root };
        Save(state, session);
        TakeOver(m, root, state, session);
        session.Complete = true;
        Save(state, session);
    }

    static void Connect(string name, string root, string state, string record, string gameDir)
    {
        var connection = new SteamConnection { Record = record, GameDir = gameDir };
        SaveConnection(state, connection);
        if (File.Exists(record)) File.Move(record, record + Aside);
        if (Directory.Exists(gameDir))
        {
            if (Directory.Exists(gameDir + Aside)) throw new IOException($"{gameDir + Aside} is in the way. Move it somewhere else and try again.");
            Directory.Move(gameDir, gameDir + Aside);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(gameDir)!);
        Win.CreateJunction(gameDir, Path.Combine(root, "App"));
        File.Copy(Path.Combine(root, "Data", "Steam", name), record);
        connection.RecordPlaced = true;
        SaveConnection(state, connection);
    }

    static void DisconnectFromSteam(Manifest m, string root, string state)
    {
        var connection = LoadConnection(state);
        var settings = LoadSession(state);
        if (connection is null && settings is null)
        {
            Show($"{m.Name} isn't connected to Steam on this PC.");
            return;
        }

        var exe = Steam.Exe;
        var wasOpen = exe is not null && Steam.IsRunning(exe);
        if (wasOpen && !AskToRestartSteam($"Steam needs to restart to disconnect {m.Name}. Restart Steam now?")) return;
        if (settings is not null) Restore(m, root, state, settings);
        if (connection is not null) Release(root, state, connection);
        if (wasOpen) Steam.Start(exe!, "-silent");
        Show($"{m.Name} is disconnected from Steam on this PC. Your portable folder hasn't changed.");
    }

    static void Release(string root, string state, SteamConnection c)
    {
        if (Steam.Exe is { } exe) Steam.Close(exe); // Steam rewrites its records as it closes, so this comes first
        if (Win.IsLink(c.GameDir))
        {
            if (c.RecordPlaced && File.Exists(c.Record))
            {
                File.Copy(c.Record, Path.Combine(root, "Data", "Steam", Path.GetFileName(c.Record)), overwrite: true);
                File.Delete(c.Record);
            }
            Directory.Delete(c.GameDir); // not recursive: removes the link, never what it points at
        }
        if (Directory.Exists(c.GameDir + Aside)) Directory.Move(c.GameDir + Aside, c.GameDir);
        if (File.Exists(c.Record + Aside)) File.Move(c.Record + Aside, c.Record);
        File.Delete(ConnectionFile(state));
    }

    static bool AskToRestartSteam(string question) =>
        MessageBox.Show(question, "Portabilizer", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    static bool SameFolder(string? a, string b) =>
        a is not null && a.TrimEnd('\\').Equals(b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static string ConnectionFile(string state) => Path.Combine(state, "steam.json");

    static SteamConnection? LoadConnection(string state) =>
        File.Exists(ConnectionFile(state)) ? JsonSerializer.Deserialize<SteamConnection>(File.ReadAllText(ConnectionFile(state))) : null;

    static void SaveConnection(string state, SteamConnection c) =>
        Win.WriteAtomic(ConnectionFile(state), JsonSerializer.Serialize(c), Encoding.UTF8);

    static void Restore(Manifest m, string root, string state, Session s)
    {
        var appDir = Path.Combine(s.Root, "App");
        foreach (var i in s.Started.OrderDescending().ToList())
        {
            var entry = m.Registry[i];
            if (s.Imported.Contains(i))
            {
                if (Win.KeyExists(entry.Key))
                {
                    var live = Path.Combine(state, $"{i}.live.reg");
                    Win.Reg("export", entry.Key, live, "/y");
                    var text = Win.ToTokens(File.ReadAllText(live, Encoding.Unicode), appDir, Win.Profile);
                    Win.WriteAtomic(Path.Combine(root, entry.File), text, Encoding.Unicode);
                    File.Delete(live);
                }
                s.Imported.Remove(i);
                Save(state, s);
            }

            if (Win.KeyExists(entry.Key)) Win.Reg("delete", entry.Key, "/f");
            Win.PruneEmptyParents(entry.Key);
            if (File.Exists(Backup(state, i)))
            {
                Win.Reg("import", Backup(state, i));
                File.Delete(Backup(state, i));
            }
            s.Started.Remove(i);
            Save(state, s);
        }

        foreach (var link in Enumerable.Reverse(s.Links).ToList())
        {
            if (Win.IsLink(link)) Directory.Delete(link); // not recursive: removes the link, never what it points at
            if (Directory.Exists(link + Aside)) Directory.Move(link + Aside, link);
            s.Links.Remove(link);
            Save(state, s);
        }

        foreach (var dir in Enumerable.Reverse(s.CreatedDirs))
            if (Directory.Exists(dir) && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);

        File.Delete(Path.Combine(state, "session.json"));
    }

    static string Backup(string state, int index) => Path.Combine(state, $"{index}.reg");

    static Session? LoadSession(string state)
    {
        var file = Path.Combine(state, "session.json");
        return File.Exists(file) ? JsonSerializer.Deserialize<Session>(File.ReadAllText(file)) : null;
    }

    static void Save(string state, Session s) =>
        Win.WriteAtomic(Path.Combine(state, "session.json"), JsonSerializer.Serialize(s), Encoding.UTF8);

    static int Elevate(Manifest m, string[] args)
    {
        const int Cancelled = 1223;
        try
        {
            Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = true, Verb = "runas", Arguments = Quote(args) })?.Dispose();
            return 0;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == Cancelled)
        {
            Show($"{m.Name} needs administrator permission to start, because it keeps settings in a PC-wide part of Windows.");
            return 1;
        }
    }

    static string Quote(string[] args) => string.Join(' ', args.Select(a => $"\"{a}\""));

    static void Show(string message)
    {
        if (Steam.UnderTest) return; // tools\smoke.ps1 runs on the owner's desktop, so it mustn't put boxes on their screen
        MessageBox.Show(message, "Portabilizer", MessageBoxButton.OK, MessageBoxImage.Warning);
    }
}
