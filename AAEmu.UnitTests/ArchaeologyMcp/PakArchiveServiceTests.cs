using AAEmu.ArchaeologyMcp;
using AAEmu.Commons.Utils.AAPak;

namespace AAEmu.UnitTests.ArchaeologyMcp;

/// <summary>
/// Defends the read-only AAPak archive service against a tiny archive created
/// in temp (no local 24.8 GB assets assumed): entry listing with regex filter
/// and result cap, bounded single-entry reads, traversal/absolute/backslash
/// name rejection, missing-entry and unconfigured behavior, deterministic
/// provenance metadata, and read-only archive handling.
/// </summary>
[NotInParallel]
public class PakArchiveServiceTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _pakPath;
    private readonly PakArchiveService _service;

    public PakArchiveServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "aaemu-arch-pak-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _pakPath = Path.Combine(_tempDir, "test.pak");

        CreateTinyPak(_pakPath);

        _service = new PakArchiveService(_pakPath, "test-version");
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_tempDir, recursive: true);
        }
        catch (IOException)
        {
            // Best-effort temp cleanup.
        }
    }

    /// <summary>
    /// Builds a minimal TypeA AAPak with a few entries using the library's own
    /// write path (NewPak + AddAsNewFile), then closes it so the service opens
    /// it read-only.
    /// </summary>
    private static void CreateTinyPak(string path)
    {
        var pak = new AAPak(path, openAsReadOnly: false, createAsNewPak: true);
        try
        {
            if (!pak.isOpen)
                throw new InvalidOperationException("failed to create test pak");
            var now = DateTime.UtcNow;
            Add(pak, "art/characters/guard.nut", "guard-data", now);
            Add(pak, "art/characters/merchant.nut", "merchant-data", now);
            Add(pak, "ui/questcontext/quest.lua", "quest-ui", now);
            Add(pak, "bin32/archeage.exe", new string('x', 2048), now);
            // Long 'a' run + non-matching tail: triggers catastrophic
            // backtracking for patterns like (a+)+$ (regression coverage).
            Add(pak, "long/" + new string('a', 60) + "X", "long-name", now);
        }
        finally
        {
            pak.ClosePak();
        }
    }

    private static void Add(AAPak pak, string name, string content, DateTime now)
    {
        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(content));
        if (!pak.AddAsNewFile(name, stream, now, now, autoSpareSpace: false, out _))
            throw new InvalidOperationException($"failed to add entry: {name}");
    }

    /// <summary>
    /// Builds a structurally valid TypeA AAPak whose FAT carries a negative
    /// entry size (simulating a corrupted file table). The trailing 512-byte
    /// encrypted header is stolen from a real pak; the FAT blocks are crafted
    /// with the library's own AES key so the header decrypts and the table
    /// parses, exposing the malformed metadata to the service.
    /// </summary>
    private static void CreateMalformedPak(string path)
    {
        var valid = Path.Combine(Path.GetTempPath(), "aaemu-arch-pak-valid-" + Guid.NewGuid().ToString("N")[..8] + ".pak");
        try
        {
            // Build a 2-entry pak so its header's fileCount matches the two
            // crafted FAT blocks below.
            var pak = new AAPak(valid, openAsReadOnly: false, createAsNewPak: true);
            try
            {
                if (!pak.isOpen)
                    throw new InvalidOperationException("failed to create source pak");
                var now = DateTime.UtcNow;
                Add(pak, "art/characters/guard.nut", "guard-data", now);
                Add(pak, "ui/questcontext/quest.lua", "quest-ui", now);
            }
            finally
            {
                pak.ClosePak();
            }

            var full = File.ReadAllBytes(valid);
            var header = full[^512..];

            var key = new byte[] { 0x32, 0x1F, 0x2A, 0xEE, 0xAA, 0x58, 0x4A, 0xB4, 0x9A, 0x6C, 0x9E, 0x09, 0xD5, 0x9E, 0x9C, 0x6F };
            var fat = new MemoryStream();
            fat.Write(AAPakFileHeader.EncryptAES(CraftBlock("art/characters/guard.nut", 0, -1), key, true));
            fat.Write(AAPakFileHeader.EncryptAES(CraftBlock("ui/questcontext/quest.lua", 0, 8), key, true));
            var fatBytes = fat.ToArray();
            var fatPadded = new byte[0x400];
            Array.Copy(fatBytes, fatPadded, fatBytes.Length);

            using var fs = File.Create(path);
            fs.Write(new byte[0x400]); // file data area
            fs.Write(fatPadded);
            fs.Write(header);
        }
        finally
        {
            File.Delete(valid);
        }
    }

    /// <summary>Builds one 0x150-byte TypeA FAT block with the given name/offset/size.</summary>
    private static byte[] CraftBlock(string name, long offset, long size)
    {
        var block = new byte[0x150];
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        Array.Copy(nameBytes, block, Math.Min(nameBytes.Length, 0x108));
        BitConverter.GetBytes(offset).CopyTo(block, 0x108);
        BitConverter.GetBytes(size).CopyTo(block, 0x110);
        BitConverter.GetBytes(size).CopyTo(block, 0x118);
        return block;
    }

    // ------------------------------------------------------- list_pak_entries

    [Test]
    public async Task ListEntries_ReturnsMatchingMetadata()
    {
        var result = _service.ListEntries("art/", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        var data = result["data"]!;
        var entries = data["entries"]!.AsArray();
        await Assert.That(entries).HasCount().EqualTo(2);
        await Assert.That(entries[0]!["name"]?.GetValue<string>()).IsEqualTo("art/characters/guard.nut");
        await Assert.That(entries[0]!["size"]?.GetValue<long>()).IsEqualTo(10);
        await Assert.That(entries[0]!["md5"]?.GetValue<string>()).HasLength().EqualTo(32);
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("list_pak_entries");
        await Assert.That(result["provenance"]?["source_id"]?.GetValue<string>()).IsEqualTo("game_pak");
        await Assert.That(result["provenance"]?["path"]?.GetValue<string>()).IsEqualTo(_pakPath);
        await Assert.That(result["provenance"]?["version"]?.GetValue<string>()).IsEqualTo("test-version");
    }

    [Test]
    public async Task ListEntries_NoPattern_ReturnsAll()
    {
        var result = _service.ListEntries(null, null);

        await Assert.That(result["data"]!["entries"]!.AsArray()).HasCount().EqualTo(5);
    }

    [Test]
    public async Task ListEntries_Limit_Truncates()
    {
        var result = _service.ListEntries(null, 2);

        var data = result["data"]!;
        await Assert.That(data["entries"]!.AsArray()).HasCount().EqualTo(2);
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["limit"]?.GetValue<int>()).IsEqualTo(2);
        await Assert.That(result["provenance"]?["truncated"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task ListEntries_InvalidRegex_ReturnsError()
    {
        var result = _service.ListEntries("[unclosed", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("invalid regex");
    }

    [Test]
    public async Task ListEntries_RegexTimeout_ReturnsDeterministicError()
    {
        // Catastrophic backtracking must fail deterministically, never hang
        // or escape as an internal error. The shared test pak contains a
        // 60-char 'a' run + 'X' tail, which drives (a+)+$ past its 2s cap.
        var result = _service.ListEntries("(a+)+$", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("regex timeout");
        await Assert.That(result["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("list_pak_entries");
    }

    [Test]
    public async Task ListEntries_MissingPak_ReturnsError()
    {
        var service = new PakArchiveService(Path.Combine(_tempDir, "nope.pak"), "test-version");
        var result = service.ListEntries(null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("not found");
    }

    // -------------------------------------------------------- read_pak_entry

    [Test]
    public async Task ReadEntry_ReadsBoundedContent()
    {
        var result = _service.ReadEntry("ui/questcontext/quest.lua", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsTrue();
        var data = result["data"]!;
        await Assert.That(data["name"]?.GetValue<string>()).IsEqualTo("ui/questcontext/quest.lua");
        await Assert.That(data["size"]?.GetValue<long>()).IsEqualTo(8);
        await Assert.That(data["bytes_read"]?.GetValue<int>()).IsEqualTo(8);
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsFalse();
        var content = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(data["content_base64"]!.GetValue<string>()));
        await Assert.That(content).IsEqualTo("quest-ui");
        await Assert.That(result["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("read_pak_entry");
    }

    [Test]
    public async Task ReadEntry_TruncatesOversizedEntry()
    {
        var result = _service.ReadEntry("bin32/archeage.exe", 100);

        var data = result["data"]!;
        await Assert.That(data["size"]?.GetValue<long>()).IsEqualTo(2048);
        await Assert.That(data["bytes_read"]?.GetValue<int>()).IsEqualTo(100);
        await Assert.That(data["truncated"]?.GetValue<bool>()).IsTrue();
        await Assert.That(data["limit"]?.GetValue<int>()).IsEqualTo(100);
        await Assert.That(result["provenance"]?["truncated"]?.GetValue<bool>()).IsTrue();
    }

    [Test]
    public async Task ReadEntry_MissingEntry_ReturnsError()
    {
        var result = _service.ReadEntry("art/characters/nope.nut", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("not found");
    }

    [Test]
    public async Task ReadEntry_MalformedNegativeSize_ReturnsDeterministicError()
    {
        // A corrupted FAT with a negative entry size must be rejected
        // deterministically, never crash on allocation (OverflowException).
        var malformed = Path.Combine(_tempDir, "malformed.pak");
        CreateMalformedPak(malformed);

        var service = new PakArchiveService(malformed, "test-version");
        var result = service.ReadEntry("art/characters/guard.nut", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("malformed archive entry");
        await Assert.That(result["provenance"]?["tool"]?.GetValue<string>()).IsEqualTo("read_pak_entry");
    }

    // ------------------------------------------------- name validation

    [Test]
    public async Task ReadEntry_TraversalName_IsRejected()
    {
        var result = _service.ReadEntry("../secret", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("invalid entry name");
    }

    [Test]
    public async Task ReadEntry_AbsoluteName_IsRejected()
    {
        var result = _service.ReadEntry("/etc/passwd", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("invalid entry name");
    }

    [Test]
    public async Task ReadEntry_BackslashName_IsRejected()
    {
        var result = _service.ReadEntry("art\\characters\\guard.nut", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("invalid entry name");
    }

    [Test]
    public async Task ReadEntry_DriveLetterName_IsRejected()
    {
        var result = _service.ReadEntry("C:/windows/system32/foo", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("invalid entry name");
    }

    [Test]
    public async Task ReadEntry_EmptyName_IsRejected()
    {
        var result = _service.ReadEntry("", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("invalid entry name");
    }

    // ------------------------------------------------- unconfigured behavior

    [Test]
    public async Task Unconfigured_ListEntries_ReturnsError()
    {
        var service = new PakArchiveService(string.Empty, "test-version");
        var result = service.ListEntries(null, null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("not configured");
    }

    [Test]
    public async Task Unconfigured_ReadEntry_ReturnsError()
    {
        var service = new PakArchiveService(string.Empty, "test-version");
        var result = service.ReadEntry("ui/questcontext/quest.lua", null);

        await Assert.That(result["ok"]?.GetValue<bool>()).IsFalse();
        await Assert.That(result["error"]?.GetValue<string>()).Contains("not configured");
    }

    [Test]
    public async Task FromEnvironment_Unset_IsNotConfigured()
    {
        var previous = Environment.GetEnvironmentVariable("ARCHEAGE_PAK_PATH");
        try
        {
            Environment.SetEnvironmentVariable("ARCHEAGE_PAK_PATH", null);
            var service = PakArchiveService.FromEnvironment();
            await Assert.That(service.IsConfigured).IsFalse();
        }
        finally
        {
            Environment.SetEnvironmentVariable("ARCHEAGE_PAK_PATH", previous);
        }
    }

    // ------------------------------------------------- read-only invariant

    [Test]
    public async Task Operations_DoNotMutateArchive()
    {
        var before = File.ReadAllBytes(_pakPath);

        _service.ListEntries(null, null);
        _service.ReadEntry("ui/questcontext/quest.lua", null);

        var after = File.ReadAllBytes(_pakPath);
        await Assert.That(after).IsEquivalentTo(before);
    }
}
