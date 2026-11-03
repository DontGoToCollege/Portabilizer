using System.Text.Json;

namespace Portabilizer.Core;

/// <summary>
/// Finds the shared .NET runtime an app needs, in Portabilizer's library or on this PC, so a copy can travel in the package.
/// .NET is MIT-licensed and free to redistribute; the launcher points the app at the copy with DOTNET_ROOT.
/// </summary>
internal static class DotNet
{
    public sealed record Framework(string Name, string Version, bool X86, string Root);
    public sealed record Need(List<(string Name, Version Version)> Frameworks, bool X86, string Label);

    public const string BaseName = "Microsoft.NETCore.App";
    static readonly string[] Notices = ["LICENSE.txt", "ThirdPartyNotices.txt"];

    public static string PcRoot(bool x86) => Path.Combine(Environment.GetFolderPath(
        x86 ? Environment.SpecialFolder.ProgramFilesX86 : Environment.SpecialFolder.ProgramFiles), "dotnet");

    /// <summary>The shared frameworks a runtimeconfig asks for, or none when the app carries its own.</summary>
    public static List<(string Name, Version Version)> Needs(string runtimeConfig)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(runtimeConfig));
            var options = doc.RootElement.GetProperty("runtimeOptions");
            if (options.TryGetProperty("includedFrameworks", out _)) return [];
            List<JsonElement> listed = options.TryGetProperty("frameworks", out var many) ? [.. many.EnumerateArray()]
                : options.TryGetProperty("framework", out var one) ? [one] : [];
            return listed.Select(f => (f.GetProperty("name").GetString()!, Version.Parse(f.GetProperty("version").GetString()!))).ToList();
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or IOException or FormatException or ArgumentException)
        {
            return []; // unreadable or a preview version: say nothing rather than guess
        }
    }

    /// <summary>
    /// The versions the app would run on, picked the way .NET rolls forward by default: the newest patch of the same minor
    /// version, or else the lowest newer minor. Earlier roots win a tie. Null when the roots lack one of them.
    /// </summary>
    public static List<Framework>? Resolve(Need need, IReadOnlyList<string> roots)
    {
        var picked = new List<Framework>();
        foreach (var (name, want) in need.Frameworks)
        {
            if (Best(name, want, roots) is not { } found) return null;
            picked.Add(new(name, found.Version, need.X86, found.Root));
            // Desktop and web frameworks sit on the base runtime of the same version.
            if (name == BaseName) continue;
            var baseRoot = roots.FirstOrDefault(r => Directory.Exists(Path.Combine(r, "shared", BaseName, found.Version)));
            if (baseRoot is null) return null;
            picked.Add(new(BaseName, found.Version, need.X86, baseRoot));
        }
        return picked.Distinct().ToList();
    }

    static (string Version, string Root)? Best(string name, Version want, IReadOnlyList<string> roots)
    {
        var have = roots
            .SelectMany(root => Directory.Exists(Path.Combine(root, "shared", name))
                ? Directory.GetDirectories(Path.Combine(root, "shared", name)).Select(d => (Name: Path.GetFileName(d), Root: root))
                : [])
            .Select(d => (Version: Version.TryParse(d.Name, out var v) ? v : null, d.Root))
            .Where(d => d.Version is { } v && v.Major == want.Major && v >= want)
            .ToList();
        if (have.Count == 0) return null;
        var minor = have.Any(d => d.Version!.Minor == want.Minor) ? want.Minor : have.Min(d => d.Version!.Minor);
        var best = have.Where(d => d.Version!.Minor == minor).Max(d => d.Version)!;
        return (best.ToString(), have.First(d => d.Version == best).Root);
    }

    /// <summary>
    /// What to copy, as (file or folder, path inside the package's Data\DotNet): the frameworks, the newest host, which reads
    /// every version, and .NET's licence notices, which must travel with it.
    /// </summary>
    public static IEnumerable<(string From, string To)> Folders(IEnumerable<Framework> frameworks)
    {
        foreach (var group in frameworks.GroupBy(f => f.X86))
        {
            var arch = group.Key ? "x86" : "x64";
            foreach (var f in group) yield return (Path.Combine(f.Root, "shared", f.Name, f.Version), Path.Combine(arch, "shared", f.Name, f.Version));
            var roots = group.Select(f => f.Root).Distinct().ToList();
            var host = roots
                .Where(r => Directory.Exists(Path.Combine(r, "host", "fxr")))
                .SelectMany(r => Directory.GetDirectories(Path.Combine(r, "host", "fxr")))
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version()).First();
            yield return (host, Path.Combine(arch, "host", "fxr", Path.GetFileName(host)));
            foreach (var notice in Notices)
                if (roots.Select(r => Path.Combine(r, notice)).FirstOrDefault(File.Exists) is { } file)
                    yield return (file, Path.Combine(arch, notice));
        }
    }
}
