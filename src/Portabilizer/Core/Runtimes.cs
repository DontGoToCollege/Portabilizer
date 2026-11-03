using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;

namespace Portabilizer.Core;

/// <summary>Copies the Visual C++ and DirectX helper files an app needs next to its programs, since another PC may not have them.</summary>
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
            // Portabilizer's own library first, so a package doesn't depend on what this PC happens to have.
            string[] sources = Library.Settings.Files == FilesSource.ThisPcOnly ? [system] : [Library.FilesDir(machine == Machine.I386), system];
            var dir = image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(image)! : mainExeDir;
            foreach (var dll in imports.Where(d => Redistributable().IsMatch(d) && !present.Contains(d)))
            {
                var source = sources.Select(s => Path.Combine(s, dll)).FirstOrDefault(File.Exists);
                if (source is null) continue;
                var copy = Path.Combine(dir, dll);
                File.Copy(source, copy);
                present.Add(dll);
                queue.Enqueue(copy); // runtime files depend on each other
            }
        }
    }

    public static Machine MachineOf(string file) => Read(file).Machine;

    public static bool IsRedistributable(string fileName) => Redistributable().IsMatch(fileName);

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

    // Visual C++ runtimes old and new, plus the DirectX extras older games need that Windows doesn't include.
    [GeneratedRegex(@"^((msvcp|msvcr|vcruntime|vcomp|concrt|vccorlib|mfc|mfcm)\d.*|d3dx9_\d+|d3dx10(_\d+)?|d3dx11_\d+|d3dcompiler_(3\d|4[0-6])|xinput1_[123]|x3daudio1_\d|xaudio2_[0-7]|xapofx1_\d|xactengine\d_\d)\.dll$", RegexOptions.IgnoreCase)]
    private static partial Regex Redistributable();
}
