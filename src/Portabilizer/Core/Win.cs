using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace Portabilizer.Core;

/// <summary>Thin wrappers over the Windows features both capture and launch rely on.</summary>
internal static class Win
{
    public const string AppToken = "{{APP}}";
    public const string ProfileToken = "{{PROFILE}}";

    public static bool IsAdmin => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public static string Profile => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string Expand(string path) => Environment.ExpandEnvironmentVariables(
        path.Replace("%DOCUMENTS%", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)));

    public static void Reg(params string[] args)
    {
        var (code, output) = Run("reg.exe", args);
        if (code != 0) throw new IOException($"reg {args[0]} failed: {output.Trim()}");
    }

    public static void CreateJunction(string link, string target)
    {
        // Quoted by hand: ArgumentList would leave a space-free path holding & unquoted for cmd.
        var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new IOException($"Couldn't link {link}: {output.Trim()}");
    }

    public static bool IsLink(string path) =>
        Directory.Exists(path) && new DirectoryInfo(path).Attributes.HasFlag(FileAttributes.ReparsePoint);

    static (int Code, string Output) Run(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        p.WaitForExit();
        return (p.ExitCode, stdout.Result + stderr.Result);
    }

    public static RegistryKey? OpenKey(string key, bool writable = false)
    {
        var slash = key.IndexOf('\\');
        var hive = key[..slash] == "HKCU" ? Registry.CurrentUser : Registry.LocalMachine;
        return hive.OpenSubKey(key[(slash + 1)..], writable);
    }

    public static bool KeyExists(string key)
    {
        using var k = OpenKey(key);
        return k is not null;
    }

    /// <summary>Removes parents left empty by a deleted key, stopping above the vendor level (HKCU\Software\Vendor).</summary>
    public static void PruneEmptyParents(string key)
    {
        var parts = key.Split('\\');
        for (var count = parts.Length - 1; count >= 3; count--)
        {
            var parent = string.Join('\\', parts[..count]);
            using (var k = OpenKey(parent))
                if (k is null || k.SubKeyCount > 0 || k.ValueCount > 0) return;
            var above = string.Join('\\', parts[..(count - 1)]);
            using var up = OpenKey(above, writable: true);
            up?.DeleteSubKey(parts[count - 1], throwOnMissingSubKey: false);
        }
    }

    static string Escaped(string path) => path.TrimEnd('\\').Replace(@"\", @"\\");

    // shortcut: only plain-text values are rewritten, not hex() or binary ones, upgrade if apps turn up that need them.
    public static string ToTokens(string regText, string appDir, string profile) => regText
        .Replace(Escaped(appDir), AppToken, StringComparison.OrdinalIgnoreCase)
        .Replace(Escaped(profile), ProfileToken, StringComparison.OrdinalIgnoreCase);

    public static string FromTokens(string regText, string appDir, string profile) => regText
        .Replace(AppToken, Escaped(appDir))
        .Replace(ProfileToken, Escaped(profile));

    public static void WriteAtomic(string path, string text, Encoding encoding)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, encoding);
        File.Move(temp, path, overwrite: true);
    }

    public static bool AnyRunningFrom(string folder)
    {
        var prefix = folder.TrimEnd('\\') + @"\";
        foreach (var p in Process.GetProcesses())
            using (p)
                if (ImagePath(p.Id) is { } image && image.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static string? ImagePath(int pid)
    {
        const uint QueryLimited = 0x1000;
        var h = OpenProcess(QueryLimited, false, pid);
        if (h == 0) return null;
        var size = 1024;
        var name = new StringBuilder(size);
        var ok = QueryFullProcessImageNameW(h, 0, name, ref size);
        CloseHandle(h);
        return ok ? name.ToString() : null;
    }

    [DllImport("kernel32", SetLastError = true)] static extern nint OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern bool QueryFullProcessImageNameW(nint process, int flags, StringBuilder name, ref int size);
    [DllImport("kernel32")] static extern bool CloseHandle(nint handle);
}
