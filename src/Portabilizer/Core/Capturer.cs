using System.Text;
using System.Text.RegularExpressions;

namespace Portabilizer.Core;

/// <summary>Writes a portable package. The installed app is only read, and the manifest goes last so a half-finished package never runs.</summary>
public static partial class Capturer
{
    public const string DisconnectFile = "Disconnect from this PC.cmd";

    public static void Run(Plan plan, string mainExe, bool includeData, string outDir, IProgress<(double Fraction, string Status)> progress)
    {
        var app = plan.App;
        var exeName = FileSafe().Replace($"{CleanName(app.Name)} Portable", "") + ".exe";
        // The chosen folder may hold other things, such as a desktop, so only these are ever added or removed.
        string[] ours = ["App", "Data", exeName, Manifest.FileName, DisconnectFile];
        if (ours.FirstOrDefault(name => Path.Exists(Path.Combine(outDir, name))) is { } clash)
            throw new IOException($"That folder already has \"{clash}\" in it. Pick another folder, or tick \"Make a folder for the app here\".");
        if (IsInside(outDir, app.InstallPath))
            throw new IOException("The portable folder can't go inside the app's own folder.");
        if (Win.AnyRunningFrom(app.InstallPath))
            throw new IOException($"{app.Name} is open. Close it and try again.");

        var needed = plan.AppBytes + plan.RuntimeBytes + (includeData ? plan.DataBytes : 0);
        var existed = Directory.Exists(outDir);
        Directory.CreateDirectory(outDir);
        var free = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outDir))!).AvailableFreeSpace;
        if (free < needed)
        {
            if (!existed) Directory.Delete(outDir);
            throw new IOException($"Not enough free space. This needs {Size(needed)} and the drive has {Size(free)}.");
        }

        try
        {
            long done = 0;
            void Copied(long bytes, string status)
            {
                done += bytes;
                progress.Report((needed == 0 ? 1 : Math.Min(1, (double)done / needed), status));
            }

            var appDir = Path.Combine(outDir, "App");
            CopyTree(app.InstallPath, appDir, Copied);
            var relative = Path.GetRelativePath(app.InstallPath, mainExe);
            if (plan.Steam is null) Runtimes.CopyInto(appDir, Path.GetDirectoryName(Path.Combine(appDir, relative))!); // Steam sets up its own games' runtimes
            foreach (var (from, to) in DotNet.Folders(plan.DotNetCopies))
                CopyTree(from, Path.Combine(outDir, "Data", "DotNet", to), Copied);

            var folders = new List<FolderLink>();
            if (includeData)
                foreach (var folder in plan.Folders)
                {
                    var source = Path.Combine("Data", "Folders", folders.Count.ToString());
                    CopyTree(folder.RealPath, Path.Combine(outDir, source), Copied);
                    folders.Add(new(folder.Target, source));
                }

            progress.Report((1, "Saving settings from the registry..."));
            var keys = new List<KeyFile>();
            if (includeData)
            {
                Directory.CreateDirectory(Path.Combine(outDir, "Data", "Registry"));
                // Steam recreates a game's PC-wide keys itself, and must not be started with administrator rights.
                foreach (var key in plan.Keys.Where(k => plan.Steam is null || k.StartsWith("HKCU")))
                {
                    var file = Path.Combine("Data", "Registry", $"{keys.Count}.reg");
                    var full = Path.Combine(outDir, file);
                    Win.Reg("export", key, full, "/y");
                    File.WriteAllText(full, Win.ToTokens(File.ReadAllText(full, Encoding.Unicode), app.InstallPath, Win.Profile), Encoding.Unicode);
                    keys.Add(new(key, file));
                }
            }

            if (plan.Steam is { } game)
            {
                Directory.CreateDirectory(Path.Combine(outDir, "Data", "Steam"));
                File.Copy(game.ManifestPath, Path.Combine(outDir, "Data", "Steam", Path.GetFileName(game.ManifestPath)));
                // Finds the exe by pattern, because a name like Crash Bandicoot™ wouldn't survive in a batch file.
                File.WriteAllText(Path.Combine(outDir, DisconnectFile), "@for %%f in (\"%~dp0*Portable.exe\") do @start \"\" \"%%f\" --disconnect\r\n", Encoding.ASCII);
            }

            File.Copy(Environment.ProcessPath!, Path.Combine(outDir, exeName));
            var slug = Slug().Replace(app.Name, "-").Trim('-');
            var id = $"{slug[..Math.Min(slug.Length, 32)]}-{Guid.NewGuid():N}";
            new Manifest(id, app.Name, app.Version, app.Publisher, Path.Combine("App", relative), app.InstallPath,
                keys.Any(k => k.Key.StartsWith("HKLM")), folders, keys, plan.Steam?.AppId, plan.Steam?.InstallDir).Save(outDir);
        }
        catch
        {
            // None of these existed before (checked above), so removing them only undoes this copy.
            foreach (var path in ours.Select(name => Path.Combine(outDir, name)))
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            if (!existed && !Directory.EnumerateFileSystemEntries(outDir).Any()) Directory.Delete(outDir);
            throw;
        }
    }

    static bool IsInside(string path, string folder) =>
        (Path.GetFullPath(path).TrimEnd('\\') + @"\").StartsWith(folder.TrimEnd('\\') + @"\", StringComparison.OrdinalIgnoreCase);

    static string CleanName(string name) => Bracketed().Replace(name, "").Trim();

    static void CopyTree(string from, string to, Action<long, string> copied)
    {
        var pending = new Stack<(DirectoryInfo From, string To)>();
        pending.Push((new DirectoryInfo(from), to));
        while (pending.Count > 0)
        {
            var (dir, target) = pending.Pop();
            Directory.CreateDirectory(target);
            foreach (var item in dir.EnumerateFileSystemInfos())
            {
                if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) continue; // links point elsewhere and could loop
                var dest = Path.Combine(target, item.Name);
                if (item is DirectoryInfo sub) pending.Push((sub, dest));
                else if (item is FileInfo file)
                {
                    file.CopyTo(dest);
                    copied(file.Length, file.Name);
                }
            }
        }
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0} MB",
        _ => $"{bytes / 1024.0:0} KB",
    };

    [GeneratedRegex(@"\(.*?\)")]
    private static partial Regex Bracketed();

    [GeneratedRegex(@"[<>:""/\\|?*]")]
    private static partial Regex FileSafe();

    [GeneratedRegex(@"[^A-Za-z0-9]+")]
    private static partial Regex Slug();
}
