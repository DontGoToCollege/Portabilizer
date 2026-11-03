using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Portabilizer.Core;

public enum DotNetSource { LibraryThenDownload, LibraryThenThisPc, ThisPcOnly }
public enum FilesSource { LibraryThenThisPc, ThisPcOnly }

public sealed class LibrarySettings
{
    public DotNetSource DotNet { get; set; } = DotNetSource.LibraryThenDownload;
    public FilesSource Files { get; set; } = FilesSource.LibraryThenThisPc;
    public bool Include32Bit { get; set; }
}

public sealed record DotNetChannel(string Version, string Phase, string LatestRuntime, string ReleasesUrl);

/// <summary>
/// Portabilizer's own store of what apps depend on, kept beside it so it travels with it and works without this PC's copies.
/// .NET is downloaded from Microsoft and checked against Microsoft's published SHA-512 before it is used.
/// </summary>
public static class Library
{
    const string IndexUrl = "https://builds.dotnet.microsoft.com/dotnet/release-metadata/releases-index.json";
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(30) };
    // Framework -> (section of Microsoft's release details, start of the zip's name). The name is matched exactly because a
    // release also lists other zips for the same machine, such as the app host pack.
    static readonly Dictionary<string, (string Section, string Zip)> DownloadKind = new()
    {
        [DotNet.BaseName] = ("runtime", "dotnet-runtime"),
        ["Microsoft.WindowsDesktop.App"] = ("windowsdesktop", "windowsdesktop-runtime"),
        ["Microsoft.AspNetCore.App"] = ("aspnetcore-runtime", "aspnetcore-runtime"),
    };

    public static string Root { get; } = FindRoot();
    public static LibrarySettings Settings { get; } = LoadSettings();

    static string FindRoot()
    {
        // tools\smoke.ps1 points this at a throwaway folder.
        if (Environment.GetEnvironmentVariable("PORTABILIZER_LIBRARY") is { Length: > 0 } test) return test;
        var home = Path.GetDirectoryName(Environment.ProcessPath)!;
        return CanWrite(home)
            ? Path.Combine(home, "Portabilizer-Library")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Portabilizer", "Library");
    }

    static bool CanWrite(string folder)
    {
        try
        {
            var probe = Path.Combine(folder, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    static string SettingsFile => Path.Combine(Root, "settings.json");

    static LibrarySettings LoadSettings()
    {
        try { return File.Exists(SettingsFile) ? JsonSerializer.Deserialize<LibrarySettings>(File.ReadAllText(SettingsFile)) ?? new() : new(); }
        catch (JsonException) { return new(); }
    }

    public static void SaveSettings()
    {
        Directory.CreateDirectory(Root);
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(Settings, new JsonSerializerOptions { WriteIndented = true }));
    }

    public static string DotNetRoot(bool x86) => Path.Combine(Root, "dotnet", x86 ? "x86" : "x64");
    public static string FilesDir(bool x86) => Path.Combine(Root, "files", x86 ? "x86" : "x64");

    public static long Size() => Directory.Exists(Root)
        ? new DirectoryInfo(Root).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).Sum(f => f.Length)
        : 0;

    /// <summary>.NET versions in the library, as "8.0.25" style strings, newest first.</summary>
    public static List<string> DotNetVersions(bool x86)
    {
        var dir = Path.Combine(DotNetRoot(x86), "shared", DotNet.BaseName);
        return Directory.Exists(dir)
            ? Directory.GetDirectories(dir).Select(Path.GetFileName).OfType<string>()
                .OrderByDescending(n => Version.TryParse(n, out var v) ? v : new Version()).ToList()
            : [];
    }

    public static int FileCount() =>
        new[] { false, true }.Select(FilesDir).Where(Directory.Exists).Sum(d => Directory.GetFiles(d).Length);

    /// <summary>
    /// The .NET an app needs, gathered the way the settings say: the library first, then a download from Microsoft into the
    /// library, then this PC. Null when none of those has it.
    /// </summary>
    internal static List<DotNet.Framework>? GatherDotNet(DotNet.Need need, Action<string> status)
    {
        var mode = Settings.DotNet;
        string[] library = [DotNetRoot(need.X86)];
        var found = mode == DotNetSource.ThisPcOnly ? null : DotNet.Resolve(need, library);
        if (found is null && mode == DotNetSource.LibraryThenDownload)
        {
            try
            {
                var channel = $"{need.Frameworks[0].Version.Major}.{need.Frameworks[0].Version.Minor}";
                // On a pool thread, so waiting here can't deadlock when called from the window's thread.
                Task.Run(() => Download(channel, need.Frameworks.Select(f => f.Name), need.X86, status)).GetAwaiter().GetResult();
                found = DotNet.Resolve(need, library);
            }
            catch (IOException) when (DotNet.Resolve(need, [DotNet.PcRoot(need.X86)]) is not null)
            {
                // offline or Microsoft unreachable, but this PC has it, so fall back below
            }
        }
        return found ?? DotNet.Resolve(need, [DotNet.PcRoot(need.X86)]);
    }

    public static async Task<List<DotNetChannel>> Channels()
    {
        try
        {
            var index = JsonNode.Parse(await Http.GetStringAsync(IndexUrl))!["releases-index"]!.AsArray();
            return index.Select(c => new DotNetChannel((string)c!["channel-version"]!, (string)c["support-phase"]!, (string)c["latest-runtime"]!, (string)c["releases.json"]!))
                .Where(c => c.Phase is not ("preview" or "go-live") && Version.TryParse(c.Version, out var v) && v >= new Version(3, 1))
                .ToList();
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NullReferenceException or InvalidOperationException)
        {
            throw new IOException($"Couldn't reach Microsoft's list of .NET versions: {e.Message}", e);
        }
    }

    /// <summary>
    /// Downloads the newest patch of one .NET version into the library. The base runtime always comes too. Returns the parts
    /// Microsoft doesn't offer as a download (older desktop runtimes come only as installers); when they're required, that's an error.
    /// </summary>
    public static async Task<List<string>> Download(string channel, IEnumerable<string> frameworks, bool x86, Action<string> status, bool required = true)
    {
        var skipped = new List<string>();
        var entry = (await Channels()).FirstOrDefault(c => c.Version == channel)
            ?? throw new IOException($"Microsoft doesn't offer .NET {channel} for download.");
        JsonNode release;
        try
        {
            var releases = JsonNode.Parse(await Http.GetStringAsync(entry.ReleasesUrl))!["releases"]!.AsArray();
            release = releases.First(r => (string?)r?["runtime"]?["version"] == entry.LatestRuntime)!;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException or NullReferenceException or InvalidOperationException)
        {
            throw new IOException($"Couldn't read Microsoft's details for .NET {channel}: {e.Message}", e);
        }

        var rid = x86 ? "win-x86" : "win-x64";
        var root = DotNetRoot(x86);
        foreach (var name in frameworks.Prepend(DotNet.BaseName).Distinct())
        {
            if (!DownloadKind.TryGetValue(name, out var kind) || release[kind.Section] is not { } part) continue;
            var version = (string?)part["version"] ?? entry.LatestRuntime;
            var target = Path.Combine(root, "shared", name, version);
            if (Directory.Exists(target)) continue;
            var label = $".NET {version} ({ShortName(name)}, {(x86 ? "32" : "64")}-bit)";
            var file = part["files"]?.AsArray().FirstOrDefault(f => (string?)f?["name"] == $"{kind.Zip}-{rid}.zip");
            if (file is null)
            {
                if (required) throw new IOException($"Microsoft only offers {label} as an installer, so Portabilizer can't download it.");
                skipped.Add(ShortName(name));
                continue;
            }
            await Fetch((string)file["url"]!, (string)file["hash"]!, root, label, status);
            if (!Directory.Exists(target)) throw new IOException($"The download of {label} didn't contain it.");
        }
        return skipped;
    }

    static string ShortName(string framework) => framework switch
    {
        "Microsoft.WindowsDesktop.App" => "desktop apps",
        "Microsoft.AspNetCore.App" => "web apps",
        _ => "base",
    };

    static async Task Fetch(string url, string sha512, string root, string label, Action<string> status)
    {
        var work = Path.Combine(Root, $".download-{Guid.NewGuid():N}");
        Directory.CreateDirectory(work);
        try
        {
            var zip = Path.Combine(work, "download.zip");
            status($"Downloading {label} from Microsoft...");
            using (var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                await using var file = File.Create(zip);
                await response.Content.CopyToAsync(file);
            }
            status($"Checking {label}...");
            await using (var check = File.OpenRead(zip))
                if (!Convert.ToHexString(await SHA512.HashDataAsync(check)).Equals(sha512, StringComparison.OrdinalIgnoreCase))
                    throw new IOException($"The download of {label} didn't match Microsoft's fingerprint, so it wasn't used.");
            var unpacked = Path.Combine(work, "unpacked");
            ZipFile.ExtractToDirectory(zip, unpacked);
            Merge(unpacked, root);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidDataException)
        {
            throw new IOException($"Couldn't download {label}: {e.Message}", e);
        }
        finally
        {
            Directory.Delete(work, recursive: true); // our own temporary folder inside the library
        }
    }

    /// <summary>Moves each version folder in whole, so a half-finished download or copy never looks like a usable version.</summary>
    static void Merge(string unpacked, string root)
    {
        var versions = Directory.Exists(Path.Combine(unpacked, "shared"))
            ? Directory.GetDirectories(Path.Combine(unpacked, "shared")).SelectMany(Directory.GetDirectories)
            : [];
        var hosts = Directory.Exists(Path.Combine(unpacked, "host", "fxr")) ? Directory.GetDirectories(Path.Combine(unpacked, "host", "fxr")) : [];
        foreach (var dir in versions.Concat(hosts))
        {
            var target = Path.Combine(root, Path.GetRelativePath(unpacked, dir));
            if (Directory.Exists(target)) continue;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(dir, target);
        }
        foreach (var file in Directory.GetFiles(unpacked, "*.txt"))
            if (!File.Exists(Path.Combine(root, Path.GetFileName(file)))) File.Copy(file, Path.Combine(root, Path.GetFileName(file)));
    }

    /// <summary>Copies the .NET versions installed on this PC into the library.</summary>
    public static void AddThisPcDotNet(Action<string> status)
    {
        foreach (var x86 in new[] { false, true })
        {
            var pc = DotNet.PcRoot(x86);
            if (!Directory.Exists(Path.Combine(pc, "shared"))) continue;
            var work = Path.Combine(Root, $".copy-{Guid.NewGuid():N}");
            try
            {
                foreach (var dir in Directory.GetDirectories(Path.Combine(pc, "shared")).SelectMany(Directory.GetDirectories)
                             .Concat(Directory.Exists(Path.Combine(pc, "host", "fxr")) ? Directory.GetDirectories(Path.Combine(pc, "host", "fxr")) : []))
                {
                    var relative = Path.GetRelativePath(pc, dir);
                    if (Directory.Exists(Path.Combine(DotNetRoot(x86), relative))) continue;
                    status($"Copying {relative} from this PC...");
                    Capturer.CopyTree(dir, Path.Combine(work, relative), (_, _) => { });
                }
                foreach (var notice in Directory.GetFiles(pc, "*.txt")) File.Copy(notice, Path.Combine(work, Path.GetFileName(notice)), overwrite: true);
                if (Directory.Exists(work)) Merge(work, DotNetRoot(x86));
            }
            finally
            {
                if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            }
        }
    }

    /// <summary>Copies this PC's Visual C++ and DirectX helper files into the library.</summary>
    public static int AddThisPcFiles()
    {
        var added = 0;
        foreach (var x86 in new[] { false, true })
        {
            var system = x86 ? Environment.GetFolderPath(Environment.SpecialFolder.SystemX86) : Environment.SystemDirectory;
            Directory.CreateDirectory(FilesDir(x86));
            foreach (var file in Directory.GetFiles(system, "*.dll").Where(f => Runtimes.IsRedistributable(Path.GetFileName(f))))
            {
                var target = Path.Combine(FilesDir(x86), Path.GetFileName(file));
                if (File.Exists(target)) continue;
                File.Copy(file, target);
                added++;
            }
        }
        return added;
    }
}
