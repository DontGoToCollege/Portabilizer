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
            var journal = Path.Combine(state, "session.json");
            if (File.Exists(journal)) Restore(m, root, state, JsonSerializer.Deserialize<Session>(File.ReadAllText(journal))!);

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
        {
            var target = Win.Expand(folder.Target);
            if (Directory.Exists(target))
            {
                if (Directory.Exists(target + Aside)) throw new IOException($"{target + Aside} is in the way. Move it somewhere else and try again.");
                Directory.Move(target, target + Aside);
            }
            MakeParents(target, s);
            s.Links.Add(target);
            Save(state, s);
            Win.CreateJunction(target, Path.Combine(root, folder.Source));
        }

        // Best effort: covers apps with their install path written into their own files. Program Files needs admin rights.
        if (!Directory.Exists(m.InstallPath))
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

        Process? main;
        const int ElevationRequired = 740;
        try { main = Process.Start(psi); }
        catch (Win32Exception e) when (e.NativeErrorCode == ElevationRequired)
        {
            main = Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = psi.WorkingDirectory, UseShellExecute = true, Verb = "runas", Arguments = Quote(args) });
        }
        main?.WaitForExit();
        main?.Dispose();

        // Many apps hand over to another of their own programs and exit, so watch the folder as well as the first process.
        var appDir = Path.Combine(root, "App");
        for (var quiet = 0; quiet < 2;)
        {
            quiet = Win.AnyRunningFrom(appDir) ? 0 : quiet + 1;
            Thread.Sleep(1500);
        }
    }

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

    static void Show(string message) => MessageBox.Show(message, "Portabilizer", MessageBoxButton.OK, MessageBoxImage.Warning);
}
