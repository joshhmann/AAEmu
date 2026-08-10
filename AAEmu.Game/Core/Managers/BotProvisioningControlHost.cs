using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Slice-4 provisioning control host (t_302b67bf) — additive test-control
/// surface for the live provision → activate → persist → deactivate round-trip
/// rig, following the BotDriveBridge precedent (AGENTS.md #9/#10: additive,
/// loopback-only, DISABLED BY DEFAULT).
///
/// A loopback-only JSON/TCP channel that drives the PRODUCTION bot path on a
/// booted game server:
///
///   provision  {username, name, race?, gender?, level?}  → HeadlessSession.Provision
///                                                          (real account+character rows,
///                                                           ActivateHeadless embodiment)
///   setLevel   {characterId, level}                       → in-memory level change (persist probe)
///   deactivate {characterId, reason?}                     → CharacterLifecycleService.Deactivate
///                                                          (leave-save persistence)
///   ping                                                  → liveness
///
/// It NEVER writes quest/gameplay state directly and never bypasses the
/// lifecycle service — every mutation flows through the same surfaces the
/// production citizen path uses. Enabled ONLY when the AAEMU_BOT_PROVISION_TEST
/// env var is 1/true; port AAEMU_BOT_PROVISION_PORT (default 1261), bound to
/// 127.0.0.1 only. Prod config never sets it.
/// </summary>
public sealed class BotProvisioningControlHost
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    public static BotProvisioningControlHost Instance { get; } = new();

    private TcpListener _listener;
    private CancellationTokenSource _cts;
    private int _port = 1261;

    public bool IsRunning { get; private set; }

    private BotProvisioningControlHost()
    {
    }

    /// <summary>Reads env config and starts the listener when enabled. No-op when disabled or already running.</summary>
    public void TryStart()
    {
        if (IsRunning)
            return;

        var envEnabled = Environment.GetEnvironmentVariable("AAEMU_BOT_PROVISION_TEST");
        if (envEnabled is not ("1" or "true" or "True"))
            return;

        var envPort = Environment.GetEnvironmentVariable("AAEMU_BOT_PROVISION_PORT");
        if (int.TryParse(envPort, out var parsedPort) && parsedPort is > 0 and < 65536)
            _port = parsedPort;

        try
        {
            _cts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            IsRunning = true;
            Logger.Info($"Provisioning control host listening on 127.0.0.1:{_port} (slice-4 live-rig surface — disabled in prod)");
            _ = AcceptLoopAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Provisioning control host failed to start");
        }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Provisioning control host accept error");
                break;
            }

            _ = Task.Run(() => ServeClientAsync(client, ct));
        }
    }

    private async Task ServeClientAsync(TcpClient client, CancellationToken ct)
    {
        try
        {
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                NewLine = "\n",
                AutoFlush = true
            };

            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null)
                    break;

                string response;
                try
                {
                    response = HandleCommand(line);
                }
                catch (Exception ex)
                {
                    response = Err($"control host error: {ex.GetType().Name}: {ex.Message}");
                }

                await writer.WriteLineAsync(response).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            Logger.Debug(ex, "Provisioning control host client session ended: {Message}", ex.Message);
        }
        finally
        {
            client.Dispose();
        }
    }

    private string HandleCommand(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var cmd = root.GetProperty("cmd").GetString();

        switch (cmd)
        {
            case "ping":
                return Ok(new { pong = true, port = _port });
            case "provision":
                return HandleProvision(root);
            case "provisionFactory":
                return HandleProvisionFactory(root);
            case "setLevel":
                return HandleSetLevel(root);
            case "deactivate":
                return HandleDeactivate(root);
            default:
                return Err($"unknown cmd '{cmd}'");
        }
    }

    private string HandleProvision(JsonElement root)
    {
        try
        {
            return HandleProvisionCore(root);
        }
        catch (Exception ex)
        {
            // The real error must land in the server log AND in the response —
            // the rig fails fast on raw errors (only the explicit boot guards
            // below say "server not ready"), so a genuine provisioning bug is
            // diagnosable instead of being retried into the void (run-6
            // lesson: a mid-boot failure was wrapped as retryable and the
            // retries then collided with the already-registered name).
            Logger.Error(ex, "Provisioning control host: provision failed");
            return Err("provision failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private string HandleProvisionCore(JsonElement root)
    {
        var username = root.GetProperty("username").GetString();
        var name = root.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(name))
            return Err("provision requires 'username' and 'name'");

        var race = Enum.TryParse<Race>(root.TryGetProperty("race", out var raceEl) ? raceEl.GetString() : null, ignoreCase: true, out var parsedRace)
            ? parsedRace
            : Race.Nuian;
        var gender = Enum.TryParse<Gender>(root.TryGetProperty("gender", out var genderEl) ? genderEl.GetString() : null, ignoreCase: true, out var parsedGender)
            ? parsedGender
            : Gender.Male;
        var level = (byte)Math.Clamp(GetInt(root, "level", 1), 1, 55);

        // Readiness guards — the rig retries on the "server not ready" marker.
        // Boot order matters: the control host can bind as soon as the DI
        // container is built, but world instances and character templates load
        // seconds later. GetWorlds() populates as each world is CREATED, while
        // MateManager is assigned moments later (WorldManager.cs:528) — the
        // main world can be complete while a SIBLING world is still mid-wiring
        // (run-8: EnterWorld iterates ALL worlds and NREd on arche_mall's
        // MateManager). And an EMPTY world table must read as not-ready too
        // (run-9: Any() over an empty table is false, so the guard passed
        // mid-boot and GetWorld(0) FATALed → ParentWorld null NRE). Every
        // world must exist AND be fully wired before provisioning.
        var worlds = WorldManager.Instance.GetWorlds();
        if (worlds.Length == 0 || worlds.Any(w => w.MateManager == null))
            return Err("server not ready (worlds still loading)");

        try
        {
            if (CharacterManager.Instance.GetTemplate(race, gender) == null)
                return Err("server not ready (character templates not loaded)");
        }
        catch
        {
            return Err("server not ready (character templates not loaded)");
        }

        var session = HeadlessSession.Provision(username, name, race, gender, level);
        return Ok(new
        {
            accountId = session.ProvisionedAccount?.AccountId,
            username = session.ProvisionedAccount?.Username,
            clientLoginBlocked = session.ProvisionedAccount?.ClientLoginBlocked,
            characterId = session.Character.Id,
            name = session.Character.Name,
            level = session.Character.Level,
            objId = session.Character.ObjId,
            worldId = session.Character.Transform.WorldId
        });
    }

    private string HandleSetLevel(JsonElement root)
    {
        var character = FindCharacter(root);
        if (character == null)
            return Err("setLevel: character not in world");

        character.Level = (byte)Math.Clamp(GetInt(root, "level", 1), 1, 55);
        return Ok(new { name = character.Name, level = character.Level });
    }

    /// <summary>
    /// M6.6 parity-seeding rig (t_747a1c44): provision through the durable
    /// factory path (BotAppearanceFactory + HeadlessSession.Provision(spec))
    /// so the live rig exercises the FULL human create-path shape —
    /// appearance blob, per-class equipment, starter bag supplies, skills,
    /// actabilities, action-bar blob. Same account/character contract as
    /// "provision" (adopt-or-create, idempotent re-provision).
    /// </summary>
    private string HandleProvisionFactory(JsonElement root)
    {
        try
        {
            return HandleProvisionFactoryCore(root);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "Provisioning control host: provisionFactory failed");
            return Err("provisionFactory failed: " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private string HandleProvisionFactoryCore(JsonElement root)
    {
        var username = root.GetProperty("username").GetString();
        var name = root.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(username))
            return Err("provisionFactory requires 'username'");

        var race = Enum.TryParse<Race>(root.TryGetProperty("race", out var raceEl) ? raceEl.GetString() : null, ignoreCase: true, out var parsedRace)
            ? parsedRace
            : Race.Nuian;
        var gender = Enum.TryParse<Gender>(root.TryGetProperty("gender", out var genderEl) ? genderEl.GetString() : null, ignoreCase: true, out var parsedGender)
            ? parsedGender
            : Gender.Male;
        var level = (byte)Math.Clamp(GetInt(root, "level", 1), 1, 55);
        // Deterministic appearance for the rig: Fight class (the canonical
        // 1-skill starter set) + fixed seed (byte-identical look across runs).
        var ability = Enum.TryParse<AbilityType>(root.TryGetProperty("ability", out var abilityEl) ? abilityEl.GetString() : null, ignoreCase: true, out var parsedAbility)
            ? parsedAbility
            : AbilityType.Fight;

        // Boot-readiness guards — the rig retries on the "server not ready"
        // marker (same rationale as HandleProvisionCore: worlds must exist
        // AND be fully wired before provisioning).
        var worlds = WorldManager.Instance.GetWorlds();
        if (worlds.Length == 0 || worlds.Any(w => w.MateManager == null))
            return Err("server not ready (worlds still loading)");

        try
        {
            if (CharacterManager.Instance.GetTemplate(race, gender) == null)
                return Err("server not ready (character templates not loaded)");
        }
        catch
        {
            return Err("server not ready (character templates not loaded)");
        }

        var spec = new BotAppearanceSpec(race, gender, ability, Seed: 0xB075EEDu, Name: name);
        var session = HeadlessSession.Provision(username, spec, level);
        return Ok(new
        {
            accountId = session.ProvisionedAccount?.AccountId,
            username = session.ProvisionedAccount?.Username,
            clientLoginBlocked = session.ProvisionedAccount?.ClientLoginBlocked,
            characterId = session.Character.Id,
            name = session.Character.Name,
            level = session.Character.Level
        });
    }

    private string HandleDeactivate(JsonElement root)
    {
        var character = FindCharacter(root);
        if (character == null)
            return Err("deactivate: character not in world (was it activated?)");

        var reason = Enum.TryParse<CharacterLifecycleReason>(
            root.TryGetProperty("reason", out var reasonEl) ? reasonEl.GetString() : null,
            ignoreCase: true, out var parsedReason)
            ? parsedReason
            : CharacterLifecycleReason.Logout;

        CharacterLifecycleService.Instance.Deactivate(character, reason);
        return Ok(new { name = character.Name, characterId = character.Id, reason = reason.ToString(), saved = true });
    }

    private static Character FindCharacter(JsonElement root)
    {
        var characterId = GetUInt(root, "characterId");
        return characterId == 0 ? null : WorldManager.Instance.GetCharacterById(characterId);
    }

    private static string Ok(object data)
        => JsonSerializer.Serialize(new { ok = true, data });

    private static string Err(string error)
        => JsonSerializer.Serialize(new { ok = false, error });

    private static uint GetUInt(JsonElement root, string name)
        => root.TryGetProperty(name, out var el) && el.TryGetUInt32(out var v) ? v : 0u;

    private static int GetInt(JsonElement root, string name, int defaultValue = 0)
        => root.TryGetProperty(name, out var el) && el.TryGetInt32(out var v) ? v : defaultValue;
}

/// <summary>
/// Slice-4 control host bootstrap — starts <see cref="BotProvisioningControlHost"/>
/// at assembly load when AAEMU_BOT_PROVISION_TEST is set (same pattern as
/// BotE2EBridgeBootstrap). When disabled (the default — prod never sets it) it
/// is a strict no-op: no thread, no socket.
/// </summary>
internal static class BotProvisioningControlHostBootstrap
{
    [ModuleInitializer]
    internal static void Init()
    {
        // ONLY the real game server process may host the control surface. A
        // process that merely LOADS AAEmu.Game.dll (e.g. the unit-test runner
        // when AAEMU_BOT_PROVISION_TEST is in its environment) must never bind
        // the port — otherwise the live rig would talk to its own in-process
        // host and the real server's bind would fail (EADDRINUSE), producing
        // an undrivable server and a self-inflicted retry loop.
        if (Assembly.GetEntryAssembly()?.GetName().Name != "AAEmu.Game")
            return;

        if (Environment.GetEnvironmentVariable("AAEMU_BOT_PROVISION_TEST") is not ("1" or "true" or "True"))
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 600 && SingletonContainer.ServiceProvider == null; i++)
                    await Task.Delay(100).ConfigureAwait(false);

                if (SingletonContainer.ServiceProvider != null)
                    BotProvisioningControlHost.Instance.TryStart();
            }
            catch
            {
                // Control host startup must never take the server down.
            }
        });
    }
}
