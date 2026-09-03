using AAEmu.ArchaeologyMcp;

// AAEmu archaeology MCP server — read-only stdio transport over the
// ArcheAge 1.2 reference data (compact.sqlite3), allowlisted repo source
// roots, and the AAPak (game_pak) archive surface. No game process, no
// WebApi, no mutation surface.
//
// Env:
//   AAEMU_ROOT             repo root (default: resolved from the app base dir)
//   ARCHEAGE_DATA_ROOT     data root (default <AAEMU_ROOT>/AAEmu.Game/Data)
//   ARCHEAGE_DB_PATH       sqlite reference DB (default <data root>/compact.sqlite3)
//   ARCHEAGE_DB_VERSION    version label for the DB source (default "1.2 r208022")
//   ARCHEAGE_PAK_PATH      AAPak (game_pak) archive path (optional; enables
//                          list_pak_entries / read_pak_entry)
//   ARCHEAGE_PAK_VERSION   provenance label for the pak source (default "1.2 r208022")
//
// Protocol: newline-delimited JSON-RPC 2.0 (MCP stdio). One request per
// line; one response per line; notifications get no response.

var catalog = SourceCatalog.FromEnvironment();
var service = new ArchaeologyService(catalog, MetadataCache.FromEnvironment());
var pakService = PakArchiveService.FromEnvironment();
var server = new ArchaeologyMcpServer(service, pakService);

while (await Console.In.ReadLineAsync() is { } line)
{
    if (string.IsNullOrWhiteSpace(line))
        continue;

    var response = await server.HandleAsync(line);
    if (response is null)
        continue;

    Console.Out.WriteLine(response);
    await Console.Out.FlushAsync();
}
