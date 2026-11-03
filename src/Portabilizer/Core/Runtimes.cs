using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace Portabilizer.Core;

/// <summary>Copies the Visual C++ runtime files an app needs next to its programs, since another PC may not have them.</summary>
internal static partial class Runtimes
{
    public static void CopyInto(string appDir, string mainExeDir)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var images = new List<string>();
        foreach (var f in Directory.EnumerateFiles(appDir, "*", options))
        {
            present.Add(Path.GetFileName(f));
            if (f.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) images.Add(f);
        }

        var queue = new Queue<string>(images);
        while (queue.Count > 0)
        {
            var image = queue.Dequeue();
            var (machine, imports) = Read(image);
            var system = machine switch
            {
                Machine.Amd64 => Environment.SystemDirectory,
                Machine.I386 => Environment.GetFolderPath(Environment.SpecialFolder.SystemX86),
                _ => null,
            };
            if (system is null) continue;
            var dir = image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(image)! : mainExeDir;
            foreach (var dll in imports.Where(d => Redistributable().IsMatch(d) && !present.Contains(d)))
            {
                var source = Path.Combine(system, dll);
                if (!File.Exists(source)) continue;
                var copy = Path.Combine(dir, dll);
                File.Copy(source, copy);
                present.Add(dll);
                queue.Enqueue(copy); // runtime files depend on each other
            }
        }
    }

    static (Machine Machine, List<string> Imports) Read(string file)
    {
        var imports = new List<string>();
        try
        {
            using var stream = File.OpenRead(file);
            using var pe = new PEReader(stream);
            var headers = pe.PEHeaders;
            if (headers.PEHeader is not { } header) return (Machine.Unknown, imports);
            var table = header.ImportTableDirectory;
            if (table.Size == 0) return (headers.CoffHeader.Machine, imports);
            var reader = pe.GetSectionData(table.RelativeVirtualAddress).GetReader();
            while (reader.RemainingBytes >= 20)
            {
                reader.Offset += 12;
                var nameRva = reader.ReadInt32();
                reader.Offset += 4;
                if (nameRva == 0) break;
                var name = new StringBuilder();
                var text = pe.GetSectionData(nameRva).GetReader();
                for (byte b; text.RemainingBytes > 0 && (b = text.ReadByte()) != 0;) name.Append((char)b);
                imports.Add(name.ToString());
            }
            return (headers.CoffHeader.Machine, imports);
        }
        catch (Exception e) when (e is BadImageFormatException or IOException or UnauthorizedAccessException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            return (Machine.Unknown, imports); // not a readable program file, nothing to copy for it
        }
    }

    [GeneratedRegex(@"^(msvcp|vcruntime|vcomp|concrt|vccorlib|mfc|mfcm)\d.*\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex Redistributable();
}
