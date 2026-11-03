using System.Text.Json;

namespace Portabilizer.Core;

/// <summary>
/// Finds the shared .NET runtime an app needs on this PC, so a copy can travel in the package.
/// .NET is MIT-licensed and free to redistribute; the launcher points the app at the copy with DOTNET_ROOT.
/// </summary>
internal static class DotNet
{
    public sealed record Framework(string Name, string Version, bool X86);

    const string BaseName = "Microsoft.NETCore.App";

    static string Root(bool x86) => Path.Combine(Environment.GetFolderPath(
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
    /// The installed versions the app would run on, picked the way .NET rolls forward by default: the newest patch of the
    /// same minor version, or else the lowest newer minor. Null when this PC lacks one of them.
    /// </summary>
    public static List<Framework>? Resolve(IEnumerable<(string Name, Version Version)> needs, bool x86)
    {
        var picked = new List<Framework>();
        foreach (var (name, want) in needs)
        {
            if (Best(name, want, x86) is not { } version) return null;
            picked.Add(new(name, version, x86));
            // Desktop and web frameworks sit on the base runtime of the same version.
            if (name == BaseName) continue;
            if (!Directory.Exists(Path.Combine(Root(x86), "shared", BaseName, version))) return null;
            picked.Add(new(BaseName, version, x86));
        }
        return picked.Distinct().ToList();
    }

    static string? Best(string name, Version want, bool x86)
    {
        var dir = Path.Combine(Root(x86), "shared", name);
        if (!Directory.Exists(dir)) return null;
        var have = Directory.GetDirectories(dir)
            .Select(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : null).OfType<Version>()
            .Where(v => v.Major == want.Major && v >= want).ToList();
        if (have.Count == 0) return null;
        var minor = have.Any(v => v.Minor == want.Minor) ? want.Minor : have.Min(v => v.Minor);
        return have.Where(v => v.Minor == minor).Max()!.ToString();
    }

    /// <summary>What to copy, as (folder on this PC, path inside the package's Data\DotNet). The newest host reads every version.</summary>
    public static IEnumerable<(string From, string To)> Folders(IEnumerable<Framework> frameworks)
    {
        foreach (var group in frameworks.GroupBy(f => f.X86))
        {
            var root = Root(group.Key);
            var arch = group.Key ? "x86" : "x64";
            foreach (var f in group) yield return (Path.Combine(root, "shared", f.Name, f.Version), Path.Combine(arch, "shared", f.Name, f.Version));
            var host = Directory.GetDirectories(Path.Combine(root, "host", "fxr"))
                .OrderByDescending(d => Version.TryParse(Path.GetFileName(d), out var v) ? v : new Version()).First();
            yield return (host, Path.Combine(arch, "host", "fxr", Path.GetFileName(host)));
        }
    }
}
