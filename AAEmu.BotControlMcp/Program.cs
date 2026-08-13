using AAEmu.BotControl;
using AAEmu.BotControlMcp;

// AAEmu bot CONTRACT-ACTION MCP sidecar (M5 stage 4, t_446228b5) — stdio
// transport. Separate process: this server only makes HTTP calls to the
// game's WebApi /api/actors/* (enqueue-only path). No engine internals.
//
// Env:
//   AAEMU_BOT_CTRL_URL    game WebApi base URL (default http://127.0.0.1:1280)
//   AAEMU_BOT_CTRL_TOKEN  the shared secret — REQUIRED, same token the
//                         game's WebApi validates (X-Auth-Token)
//
// Protocol: newline-delimited JSON-RPC 2.0 (MCP stdio). One request per
// line; one response per line; notifications get no response.

var baseUrl = Environment.GetEnvironmentVariable("AAEMU_BOT_CTRL_URL") ?? "http://127.0.0.1:1280";
var token = Environment.GetEnvironmentVariable("AAEMU_BOT_CTRL_TOKEN") ?? string.Empty;

var server = new ActionMcpServer(new BotControlClient(baseUrl, token));

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
