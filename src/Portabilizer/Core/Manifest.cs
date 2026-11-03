using System.Text.Json;

namespace Portabilizer.Core;

public sealed record FolderLink(string Target, string Source);
public sealed record KeyFile(string Key, string File);

/// <summary>Describes a portable package. It sits next to the exe, and its presence switches that exe into launcher mode.</summary>
public sealed record Manifest(
    string Id, string Name, string Version, string Publisher,
    string MainExe, string InstallPath, bool NeedsAdmin,
    List<FolderLink> Folders, List<KeyFile> Registry,
    string? SteamAppId = null, string? SteamInstallDir = null)
{
    public const string FileName = "portable.json";
    static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static Manifest Load(string root) =>
        JsonSerializer.Deserialize<Manifest>(File.ReadAllText(Path.Combine(root, FileName)), Options)
        ?? throw new InvalidDataException("portable.json is empty.");

    public void Save(string root) =>
        File.WriteAllText(Path.Combine(root, FileName), JsonSerializer.Serialize(this, Options));
}
