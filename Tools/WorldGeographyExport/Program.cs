using System.Text.Json;
using System.Text.RegularExpressions;
using AAEmu.Commons.Utils.AAPak;

// Offline, bounded, read-only extraction. No game managers or world are started.
if (args.Length != 2)
    throw new ArgumentException("Usage: WorldGeographyExport <game_pak> <empty-output-directory>");
var output = Path.GetFullPath(args[1]);
if (Directory.Exists(output) && Directory.EnumerateFileSystemEntries(output).Any())
    throw new IOException("Output directory must be empty; existing files will not be overwritten.");
Directory.CreateDirectory(output);
var pattern = new Regex(@"^game/worlds/main_world/(world\.xml|level_design/zone/[0-9]+/client/(housing|subzone)_area\.xml|zone/[0-9]+/layers/[^/]+\.lyr)$", RegexOptions.CultureInvariant);
var pak = new AAPak(args[0], true, false);
if (!pak.isOpen)
    throw new IOException("Cannot open game_pak read-only.");
try
{
    var selected = pak.pakFiles.Where(p => pattern.IsMatch(p.Key)).OrderBy(p => p.Key, StringComparer.Ordinal).ToArray();
    const long maxEntry = 8 * 1024 * 1024;
    const long maxTotal = 64 * 1024 * 1024;
    if (selected.Length > 1000 || selected.Any(p => p.Value.size > maxEntry) || selected.Sum(p => (long)p.Value.size) > maxTotal)
        throw new IOException("Geography extraction exceeds the file count or byte bounds.");
    var entries = new List<object>();
    foreach (var (name, entry) in selected)
    {
        var destination = Path.Combine(output, name.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        using var input = pak.ExportFileAsStream(entry);
        using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write);
        input.CopyTo(target);
        if (target.Length != entry.size)
            throw new IOException($"Unexpected entry size: {name}");
        entries.Add(new { path = name, bytes = entry.size });
    }
    File.WriteAllText(Path.Combine(output, "inventory.json"), JsonSerializer.Serialize(new
    {
        world = "main_world", read_only = true, entries,
        scope = "root client area XML, world XML, all packaged zone/layers XML; locale overrides excluded"
    }, new JsonSerializerOptions { WriteIndented = true }));
    Console.WriteLine($"Extracted {selected.Length} bounded geography entries read-only; source archive unchanged.");
}
finally { pak.ClosePak(); }
