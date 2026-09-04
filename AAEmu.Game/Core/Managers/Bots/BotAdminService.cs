using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

using Microsoft.Extensions.DependencyInjection;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// GM bot admin operations (P1 live-ops card t_f216710e) — the testable core
/// behind the /bot command surface (add / remove / list / here / go).
///
/// Composition rule (AGENTS.md #9/#10): every operation goes through the
/// existing PlayerBotManager registry + production provisioning
/// (HeadlessSession.Provision) + lifecycle adapter — no parallel bot path.
///
///   Add/Here → provision (idempotent adopt-or-create) → spawn → activate
///              → fidelity Full (single-step ladder) → roam route → wake
///   Remove   → clear roam route → deactivate (lifecycle leave-save)
///              → drop registry entry (no orphan rows)
///   Go       → terrain-clamp target (post-hotfix coords) → teleport
///              (transform + region graph) → re-arm roam route → wake
///
/// Constructor deps are injectable so the unit rig exercises the full
/// operation flow with fakes (BotPresenceCoordinatorTests convention); the
/// script layer resolves the production wiring via
/// <see cref="FromContainer"/>.
/// </summary>
public sealed record BotAdminCommandResult(bool Success, string Message);

/// <summary>
/// Structured per-bot snapshot for the control API/MCP surface (P1
/// t_2ea94a20) — the machine-readable sibling of the GM command's
/// human-readable <see cref="BotAdminService.List"/> output.
/// </summary>
public sealed record BotStatusRecord(string Name, uint Id, string State, string Fidelity, float X, float Y, float Z);

public sealed class BotAdminService
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Managed account used for GM-commanded bots. A SINGLE stable account so
    /// HeadlessSession.Provision's adopt-or-create idempotency applies across
    /// add → remove → re-add (a name owned by this account is re-adopted, not
    /// NameAlreadyExists'd).
    /// </summary>
    public const string GmBotAccountName = "bot_managed_gm_001";

    /// <summary>Level for GM-added bots (matches the presence demo set).</summary>
    public const byte GmBotLevel = 5;

    /// <summary>Default patrol radius around a bot's home (matches the presence demo).</summary>
    public const float GmRoamRadius = 30f;

    private readonly IPlayerBotManager _manager;
    private readonly IPlayerBotScheduler _scheduler;
    private readonly IPopulationDirector _director;
    private readonly BotRoamStepExecutor _stepExecutor;
    private readonly Func<string, string, Race, Gender, byte, HeadlessSession> _provisioner;
    private readonly Func<Vector3, uint, Vector3> _terrainResolver;
    private readonly Func<Vector3, uint, float> _groundHeightProvider;
    private readonly Func<string, bool> _nameIsTaken;
    private readonly Action<Character> _regionUpdater;

    /// <summary>
    /// DI-friendly constructor. <paramref name="provisioner"/> defaults to the
    /// production provisioning path; <paramref name="terrainResolver"/> to the
    /// WorldManager heightmap clamp (the Z-wedge fix shape);
    /// <paramref name="groundHeightProvider"/> to the same WorldManager
    /// height probe the roam executor uses; and <paramref name="nameIsTaken"/>
    /// to the NameManager registry probe.
    /// Tests inject fakes for all four (no DB, no singletons).
    /// </summary>
    public BotAdminService(
        IPlayerBotManager manager,
        IPlayerBotScheduler scheduler,
        IPopulationDirector director,
        BotRoamStepExecutor stepExecutor,
        Func<string, string, Race, Gender, byte, HeadlessSession>? provisioner = null,
        Func<Vector3, uint, Vector3>? terrainResolver = null,
        Func<Vector3, uint, float>? groundHeightProvider = null,
        Func<string, bool>? nameIsTaken = null,
        Action<Character>? regionUpdater = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        _director = director ?? throw new ArgumentNullException(nameof(director));
        _stepExecutor = stepExecutor ?? throw new ArgumentNullException(nameof(stepExecutor));
        _provisioner = provisioner ?? ((account, name, race, gender, level) =>
            HeadlessSession.Provision(account, name, race, gender, level));
        _terrainResolver = terrainResolver ?? ClampToTerrain;
        _groundHeightProvider = groundHeightProvider ?? BotPresenceCoordinator.DefaultGroundHeightProvider;
        _nameIsTaken = nameIsTaken ?? (n => NameManager.Instance.GetCharacterId(n) != 0);
        // Region-graph update after a teleport — the Unit.CheckMovedPosition
        // facility. Injectable so the rig can record it without the singleton.
        _regionUpdater = regionUpdater ?? (c => WorldManager.Instance.AddVisibleObject(c));
    }

    /// <summary>
    /// Production wiring: resolves the registered bot singletons from the DI
    /// container (the same lookup BotPresenceBootstrap uses). The script layer
    /// calls this from its Execute() — scripts are runtime-compiled with
    /// parameterless ctors, so they cannot take ctor deps.
    /// </summary>
    public static BotAdminService FromContainer()
    {
        var sp = SingletonContainer.ServiceProvider
            ?? throw new InvalidOperationException("BotAdminService: DI container not ready");
        return new BotAdminService(
            sp.GetRequiredService<IPlayerBotManager>(),
            sp.GetRequiredService<IPlayerBotScheduler>(),
            sp.GetRequiredService<IPopulationDirector>(),
            sp.GetRequiredService<BotRoamStepExecutor>());
    }

    /// <summary>Registry + embodied state snapshot: name, id, state, fidelity, position.</summary>
    public BotAdminCommandResult List()
    {
        var runtimes = _manager.GetAll();
        if (runtimes.Count == 0)
            return new BotAdminCommandResult(true, "No player bots registered.");

        var lines = new List<string>
        {
            $"Player bots: {runtimes.Count} registered ({_manager.ActiveCount} active)"
        };
        foreach (var runtime in runtimes.OrderBy(r => r.Character.Name))
        {
            var pos = runtime.Character.Transform.World.Position;
            var fidelity = _director.GetFidelity(runtime.CharacterId);
            lines.Add(
                $"  {runtime.Character.Name} (id {runtime.CharacterId}) [{runtime.State}] " +
                $"fidelity={fidelity} @ {pos.X:F1}/{pos.Y:F1}/{pos.Z:F1}");
        }

        return new BotAdminCommandResult(true, string.Join("\n", lines));
    }

    /// <summary>
    /// Structured registry snapshot (control API/MCP surface — t_2ea94a20):
    /// one record per registered bot with embodied state, fidelity and
    /// position, sorted by name. The GM command's <see cref="List"/> keeps
    /// its human-readable string; both frontends share this single core.
    /// </summary>
    public IReadOnlyList<BotStatusRecord> ListStatus()
    {
        var runtimes = _manager.GetAll();
        return [.. runtimes
            .OrderBy(r => r.Character.Name)
            .Select(r =>
            {
                var pos = r.Character.Transform.World.Position;
                return new BotStatusRecord(
                    r.Character.Name,
                    r.CharacterId,
                    r.State.ToString(),
                    _director.GetFidelity(r.CharacterId).ToString(),
                    pos.X, pos.Y, pos.Z);
            })];
    }

    /// <summary>
    /// Provisions a bot via the production HeadlessSession path (idempotent —
    /// an existing row owned by the GM bot account is adopted, not duplicated),
    /// registers + embodies it, assigns Full fidelity (visible, roaming), arms
    /// a patrol route around <paramref name="home"/> (or the bot's spawn
    /// position when null) and wakes the scheduler.
    /// </summary>
    public BotAdminCommandResult Add(string name, Vector3? home = null)
    {
        name = name.Trim();
        if (name.Length == 0)
            return new BotAdminCommandResult(false, "A bot name is required.");

        // Idempotent at the registry level: already known → report state.
        var existing = FindByName(name);
        if (existing != null)
        {
            if (existing.State == PlayerBotState.Active)
                return new BotAdminCommandResult(true,
                    $"Bot '{name}' (id {existing.CharacterId}) is already present and active.");

            // Registered but not embodied — re-activate the existing record
            // (no new provision, no duplicate row).
            if (!_manager.Activate(existing.CharacterId,
                    new BotContext { BotId = existing.CharacterId, Name = name }, "gm-command"))
                return new BotAdminCommandResult(false,
                    $"Activation failed for existing bot '{name}' — see server log.");

            var homePos = home ?? _terrainResolver(existing.Character.Transform.World.Position,
                existing.Character.Transform.ZoneId);
            if (home.HasValue)
            {
                var clamped = _terrainResolver(home.Value, existing.Character.Transform.ZoneId);
                homePos = clamped;
                existing.Character.Transform.Local.SetPosition(clamped.X, clamped.Y, clamped.Z);
                _regionUpdater(existing.Character);
            }
            ArmRoam(existing.Character, homePos);
            return new BotAdminCommandResult(true,
                $"Bot '{name}' (id {existing.CharacterId}) re-activated, roaming around {homePos.X:F0}/{homePos.Y:F0}/{homePos.Z:F0}.");
        }

        // Fresh provision — production path, adopt-or-create (restart-idempotent).
        HeadlessSession session;
        try
        {
            session = _provisioner(GmBotAccountName, name, Race.Nuian, Gender.Male, GmBotLevel);
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "BotAdmin: provision failed for {Name}", name);
            return new BotAdminCommandResult(false, $"Provision failed for '{name}': {ex.Message}");
        }

        var character = session.Character;
        if (!_manager.Spawn(character, "gm-command"))
            return new BotAdminCommandResult(false,
                $"Spawn refused for '{name}' — already registered?");

        if (!_manager.Activate(character.Id,
                new BotContext { BotId = character.Id, Name = name }, "gm-command"))
            return new BotAdminCommandResult(false,
                $"Activation failed for '{name}' — see server log.");

        // Added bots default to Full (visible, roaming) — single-step ladder
        // (Dormant → Reduced → Full; the director rejects non-adjacent jumps).
        _director.TrySetFidelity(character.Id, BotFidelity.Reduced, "gm-command");
        var full = _director.TrySetFidelity(character.Id, BotFidelity.Full, "gm-command");
        var fidelityNote = full == FidelityTransitionResult.Applied
            ? string.Empty
            : $" (fidelity Full: {full})";

        var spawnHome = home ?? _terrainResolver(character.Transform.World.Position, character.Transform.ZoneId);
        if (home.HasValue)
        {
            var clamped = _terrainResolver(home.Value, character.Transform.ZoneId);
            spawnHome = clamped;
            character.Transform.Local.SetPosition(clamped.X, clamped.Y, clamped.Z);
            _regionUpdater(character);
        }
        ArmRoam(character, spawnHome);

        return new BotAdminCommandResult(true,
            $"Bot '{name}' (id {character.Id}) added — Full fidelity, roaming around " +
            $"{spawnHome.X:F0}/{spawnHome.Y:F0}/{spawnHome.Z:F0}.{fidelityNote}");
    }

    /// <summary>
    /// Spawns a bot at the issuing GM's position (<c>/bot here</c>). Name is
    /// auto-generated (Bot01..Bot99, first free) when not given.
    /// </summary>
    public BotAdminCommandResult Here(Vector3 gmPosition, string? name = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            name = NextFreeBotName();
        if (name == null)
            return new BotAdminCommandResult(false,
                "Could not auto-generate a free bot name (tried Bot01..Bot99).");

        return Add(name, gmPosition);
    }

    /// <summary>
    /// Removes a bot by name or id: clears its patrol route, deactivates via
    /// the lifecycle (leave-save — <see cref="CharacterLifecycleService.Deactivate"/>
    /// persists the character), then drops the registry entry. Idempotent:
    /// an unknown name/id returns a friendly error, never throws.
    /// </summary>
    public BotAdminCommandResult Remove(string nameOrId)
    {
        var runtime = FindByNameOrId(nameOrId);
        if (runtime == null)
            return new BotAdminCommandResult(false, $"No bot found matching '{nameOrId}'.");

        var name = runtime.Character.Name;

        // Stop the patrol loop first so the executor stops walking the bot.
        _stepExecutor.SetRoamRoute(runtime.Character, null);

        if (runtime.State == PlayerBotState.Active)
        {
            if (!_manager.Deactivate(runtime.CharacterId, "gm-command-remove"))
                return new BotAdminCommandResult(false,
                    $"Deactivation failed for '{name}' — see server log.");
        }

        if (!_manager.Remove(runtime.CharacterId))
            return new BotAdminCommandResult(false,
                $"Registry drop failed for '{name}' — see server log.");

        return new BotAdminCommandResult(true,
            $"Bot '{name}' (id {runtime.CharacterId}) removed — deactivated, leave-saved, no orphan rows.");
    }

    /// <summary>
    /// Relocates a bot's patrol home to explicit coords (<c>/bot go</c>):
    /// terrain-clamps the target (post-hotfix coords — the Z-wedge lesson:
    /// routes must be built around terrain-Z), teleports the character
    /// (transform + region graph, the Unit.CheckMovedPosition facility), then
    /// re-arms the roam route around the new home and wakes the scheduler.
    /// </summary>
    public BotAdminCommandResult Go(string nameOrId, Vector3 target)
    {
        var runtime = FindByNameOrId(nameOrId);
        if (runtime == null)
            return new BotAdminCommandResult(false, $"No bot found matching '{nameOrId}'.");

        var character = runtime.Character;
        var clamped = _terrainResolver(target, character.Transform.ZoneId);

        // Teleport: set the transform + update region membership so clients in
        // the new area start receiving the bot's presence (the same facility
        // Unit.CheckMovedPosition uses for real client movement).
        character.Transform.Local.SetPosition(clamped.X, clamped.Y, clamped.Z);
        _regionUpdater(character);

        ArmRoam(character, clamped);
        return new BotAdminCommandResult(true,
            $"Bot '{character.Name}' (id {character.Id}) relocated — patrol home now " +
            $"{clamped.X:F0}/{clamped.Y:F0}/{clamped.Z:F0}.");
    }

    /// <summary>
    /// Re-arms the patrol route around <paramref name="home"/> and wakes the
    /// scheduler (idempotent Start — the scheduler's Exchange guard means an
    /// already-running scheduler is untouched).
    /// </summary>
    private void ArmRoam(Character character, Vector3 home)
    {
        // Terrain-aware rounded-square patrol loop (same shape the presence
        // demo uses; seed per character so bots spread instead of syncing).
        // The zone + height probe ride the injectable seams so the rig stays
        // singleton-free (WorldManager is DI-only on the merged lineage).
        var route = BotPresenceCoordinator.BuildRoamRoute(home, GmRoamRadius, (int)character.Id,
            character.Transform.ZoneId, _groundHeightProvider);
        _stepExecutor.SetRoamRoute(character, route);
        _scheduler.Start();
        _scheduler.Wake(character.Id);
    }

    private string? NextFreeBotName()
    {
        for (var i = 1; i <= 99; i++)
        {
            var candidate = $"Bot{i:D2}";
            if (!_nameIsTaken(candidate) && FindByName(candidate) == null)
                return candidate;
        }

        return null;
    }

    private PlayerBotRuntime? FindByName(string name)
        => _manager.GetAll().FirstOrDefault(r =>
            r.Character.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    private PlayerBotRuntime? FindByNameOrId(string nameOrId)
    {
        if (uint.TryParse(nameOrId, out var id) && _manager.TryGet(id, out var byId))
            return byId;
        return FindByName(nameOrId);
    }

    /// <summary>
    /// Default terrain clamp — the Z-wedge fix shape (t_d7e45251 lesson):
    /// snap Z to the heightmap when it deviates, exactly like the roam
    /// executor's ground clamp. Treats 0 height as "no heightmap data" and
    /// leaves the input untouched (same no-op semantics as the executor).
    /// </summary>
    private static Vector3 ClampToTerrain(Vector3 pos, uint zoneId)
    {
        var z = WorldManager.Instance.GetReferenceHeight(null, pos.X, pos.Y, pos.Z, zoneId);
        return z != 0f && Math.Abs(z - pos.Z) > 0.05f
            ? new Vector3(pos.X, pos.Y, z)
            : pos;
    }
}
