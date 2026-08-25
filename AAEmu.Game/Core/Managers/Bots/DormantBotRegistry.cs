using System.Diagnostics;
using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// A dormant bot citizen: the durable characters row of a managed bot
/// account, currently NOT embodied in any world (G2-A5 true dormancy).
/// </summary>
/// <param name="CharacterId">The characters.id row.</param>
/// <param name="Name">The characters.name display name.</param>
public sealed record DormantBotSpec(uint CharacterId, string Name);

/// <summary>
/// Percentile snapshot of dormant-spec materialization wall-clock (G2-A5
/// acceptance instrumentation): row-load → home restore → activate, i.e.
/// <see cref="DormantBotRegistry.Materialize"/> success duration.
/// </summary>
public readonly record struct MaterializationLatencySnapshot(
    long SampleCount, double P50Ms, double P95Ms, double P99Ms, double MaxMs);

/// <summary>
/// Discovery seam for dormant bot specs (G2-A5). The production source is a
/// SQL join over characters ↔ managed bot accounts
/// (<see cref="BotAccountProvisioningService.ManagedUsernamePrefix"/>);
/// tests stub this interface.
/// </summary>
public interface IDormantBotSource
{
    /// <summary>All dormant-capable bot rows currently known to the store.</summary>
    IReadOnlyList<DormantBotSpec> ListSpecs();
}

/// <summary>
/// Home-position lookup seam for dematerialized bots (G2-A5). A dormant spec
/// has no live Transform, so proximity materialization needs its last recorded
/// home from the playerbot_metadata store. Tests stub this interface.
/// </summary>
public interface IDormantBotHomeSource
{
    bool TryGetHome(uint characterId, out uint worldId, out Vector3 position);
}

/// <summary>
/// Production home source: reads aaemu_game.playerbot_metadata via
/// <see cref="PlayerBotMetadataStore"/> (cache-first, never throws).
/// </summary>
public sealed class PlayerBotMetadataHomeSource : IDormantBotHomeSource
{
    public static PlayerBotMetadataHomeSource Instance { get; } = new();

    public bool TryGetHome(uint characterId, out uint worldId, out Vector3 position)
    {
        var metadata = PlayerBotMetadataStore.Instance.GetForRead(characterId);
        if (!metadata.HasHome)
        {
            worldId = 0;
            position = default;
            return false;
        }

        worldId = metadata.HomeWorldId;
        position = new Vector3(metadata.HomeX, metadata.HomeY, metadata.HomeZ);
        return true;
    }
}

/// <summary>
/// Production dormant-bot discovery: SQL join over aaemu_game.characters and
/// aaemu_login.users restricted to managed bot accounts
/// (<see cref="BotAccountProvisioningService.ManagedUsernamePrefix"/> +
/// account_type = HeadlessBot) that are not deleted. The game server's MySQL
/// connection reaches both schemas on the same instance (the
/// BotAccountProvisioningService precedent).
/// </summary>
public sealed class MySqlDormantBotSource : IDormantBotSource
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public IReadOnlyList<DormantBotSpec> ListSpecs()
    {
        try
        {
            using var connection = MySQL.CreateConnection();
            using var command = connection.CreateCommand();
            command.CommandText = BuildListSql();
            command.Parameters.AddWithValue("@prefix", BotAccountProvisioningService.ManagedUsernamePrefix + "%");
            command.Parameters.AddWithValue("@botType", (byte)BotAccountType.HeadlessBot);

            var specs = new List<DormantBotSpec>();
            using var reader = command.ExecuteReader();
            while (reader.Read())
                specs.Add(new DormantBotSpec(reader.GetUInt32("id"), reader.GetString("name")));
            return specs;
        }
        catch (Exception e)
        {
            Logger.Error(e, "DormantBots: ListSpecs failed — reading as empty");
            return [];
        }
    }

    internal static string BuildListSql()
        => "SELECT c.`id`, c.`name` FROM `characters` c " +
           "INNER JOIN aaemu_login.users u ON u.`id` = c.`account_id` " +
           "WHERE u.`account_type` = @botType AND u.`username` LIKE @prefix " +
           "AND c.`delete_time` = '0001-01-01 00:00:00'";
}

/// <summary>
/// True-dormancy registry (G2-A5): tracks which managed bot citizens are
/// currently DEMATERIALIZED (no world presence, only their MySQL row) and
/// rematerializes them on demand.
///
///  * <see cref="ListSpecs"/> — lazily discovers specs once through
///    <see cref="IDormantBotSource"/>, then keeps the set in memory:
///    <see cref="Dematerialize"/> adds a spec back, <see cref="Materialize"/>
///    removes it. Specs whose character is embodied in the manager registry
///    are filtered out so a stale discovery never double-reports.
///  * <see cref="Materialize"/> — loads the ordinary Character row through
///    the SAME loader the HeadlessSession bridge adoption path uses
///    (Character.Load(characterId), HeadlessSession.Provision fresh:false),
///    restores the metadata home position when present, then embodies through
///    IPlayerBotManager.Activate → PlayerBotLifecycleAdapter →
///    CharacterLifecycleService.ActivateHeadless (the shared entry core).
///  * <see cref="Dematerialize"/> — IPlayerBotManager.Deactivate →
///    CharacterLifecycleService.Deactivate (despawn, world drop, DB save),
///    then the spec returns to the registry.
///
/// No parallel gameplay path (AGENTS.md #9/#10): everything rides the
/// ordinary manager + lifecycle seams.
/// </summary>
public sealed class DormantBotRegistry
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Manager owner tag used for spawn/activate/deactivate claims.</summary>
    public const string Owner = "true-dormancy";

    private readonly IPlayerBotManager _manager;
    private readonly IDormantBotSource _source;
    private readonly Func<uint, Character?> _characterLoader;
    private readonly IDormantBotHomeSource? _homeSource;
    private readonly object _lock = new();
    private Dictionary<uint, DormantBotSpec>? _dormant;

    // Acceptance instrumentation (G2-A5): wall-clock of each SUCCESSFUL
    // Materialize (row-load → home restore → activate). Purely passive —
    // sampled only on the materialization path, never gates behavior.
    private readonly object _latencyLock = new();
    private readonly SampleRing _latencyRing = new();

    public DormantBotRegistry(
        IPlayerBotManager manager,
        IDormantBotSource source,
        Func<uint, Character?>? characterLoader = null,
        IDormantBotHomeSource? homeSource = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _characterLoader = characterLoader ?? (id => Character.Load(id));
        _homeSource = homeSource;
    }

    /// <summary>
    /// Current dormant specs: lazily discovered from the source, minus
    /// anything currently embodied in the manager, plus everything returned
    /// by <see cref="Dematerialize"/>.
    /// </summary>
    public IReadOnlyList<DormantBotSpec> ListSpecs()
    {
        EnsureDiscovered();
        lock (_lock)
        {
            var result = new List<DormantBotSpec>(_dormant!.Count);
            foreach (var spec in _dormant.Values)
            {
                // A stale discovery entry must never shadow an embodied bot.
                if (_manager.TryGet(spec.CharacterId, out var runtime) &&
                    runtime!.State == PlayerBotState.Active)
                    continue;
                result.Add(spec);
            }

            return result;
        }
    }

    /// <summary>True when the id is tracked as dormant right now.</summary>
    public bool IsDormant(uint characterId)
    {
        EnsureDiscovered();
        lock (_lock)
            return _dormant!.ContainsKey(characterId);
    }

    /// <summary>
    /// Rematerializes a dormant spec into the live world. Loads the character
    /// row (adoption-path loader), restores the recorded home position when
    /// present, and activates headlessly through the shared lifecycle path.
    /// </summary>
    /// <returns>True when the bot is now embodied.</returns>
    public bool Materialize(DormantBotSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);

        if (_manager.TryGet(spec.CharacterId, out var existing) &&
            existing!.State == PlayerBotState.Active)
            return false; // already embodied

        var latencyStopwatch = Stopwatch.StartNew();

        var character = _characterLoader(spec.CharacterId);
        if (character == null)
        {
            Logger.Warn("True dormancy: materialize refused — characters row {CharacterId} ('{Name}') missing or deleted",
                spec.CharacterId, spec.Name);
            return false;
        }

        RestoreHomePosition(character);

        // Register with the manager first (a previously dematerialized bot is
        // already registered as Deactivated — reuse that record).
        if (!_manager.TryGet(spec.CharacterId, out _) && !_manager.Spawn(character, Owner))
            return false;

        if (!_manager.Activate(
                spec.CharacterId,
                new BotContext { BotId = spec.CharacterId, Name = character.Name },
                Owner))
            return false;

        lock (_lock)
            _dormant?.Remove(spec.CharacterId);

        latencyStopwatch.Stop();
        lock (_latencyLock)
            _latencyRing.Add(latencyStopwatch.Elapsed.TotalMilliseconds);

        Logger.Info("True dormancy: materialized '{Name}' (id {CharacterId}) in {LatencyMs:F0}ms",
            character.Name, spec.CharacterId, latencyStopwatch.Elapsed.TotalMilliseconds);
        return true;
    }

    /// <summary>
    /// Dematerializes an embodied bot: Deactivate (despawn + world drop +
    /// persist through CharacterLifecycleService), then the spec returns to
    /// this registry for later rematerialization.
    /// </summary>
    /// <returns>True when the bot is no longer embodied and tracked as dormant.</returns>
    public bool Dematerialize(Character bot)
    {
        ArgumentNullException.ThrowIfNull(bot);

        if (!_manager.Deactivate(bot.Id, Owner))
            return false;

        EnsureDiscovered();
        lock (_lock)
            _dormant![bot.Id] = new DormantBotSpec(bot.Id, bot.Name);

        Logger.Info("True dormancy: dematerialized '{Name}' (id {CharacterId}) — spec returned to registry",
            bot.Name, bot.Id);
        return true;
    }

    /// <summary>
    /// Recorded home position of a dormant spec (playerbot_metadata seam).
    /// False when no home source is wired or the row has no home.
    /// </summary>
    public bool TryGetHome(uint characterId, out uint worldId, out Vector3 position)
    {
        if (_homeSource == null)
        {
            worldId = 0;
            position = default;
            return false;
        }

        return _homeSource.TryGetHome(characterId, out worldId, out position);
    }

    /// <summary>
    /// Percentile snapshot of successful-materialization wall-clock
    /// (acceptance instrumentation seam — read by the E2E bridge metrics).
    /// </summary>
    public MaterializationLatencySnapshot GetMaterializationLatency()
    {
        lock (_latencyLock)
        {
            var (count, p50, p95, p99, max) = _latencyRing.Summarize();
            return new MaterializationLatencySnapshot(count, p50, p95, p99, max);
        }
    }

    /// <summary>Lazily runs the one-time source discovery.</summary>
    private void EnsureDiscovered()
    {
        if (_dormant != null)
            return;
        lock (_lock)
        {
            if (_dormant != null)
                return;
            var discovered = new Dictionary<uint, DormantBotSpec>();
            foreach (var spec in _source.ListSpecs())
            {
                // A spec whose character is already embodied is not dormant —
                // discovery can race a boot-time provisioning.
                if (_manager.TryGet(spec.CharacterId, out var runtime) &&
                    runtime!.State == PlayerBotState.Active)
                    continue;
                discovered[spec.CharacterId] = spec;
            }
            _dormant = discovered;
        }
    }

    /// <summary>Applies the recorded metadata home position, when one exists.</summary>
    private void RestoreHomePosition(Character character)
    {
        if (_homeSource == null ||
            !_homeSource.TryGetHome(character.Id, out _, out var home) ||
            character.Transform == null)
            return;

        character.Transform.Local.SetPosition(home);
        Logger.Debug("True dormancy: restored home position for {CharacterName} (id {CharacterId}) at {Home}",
            character.Name, character.Id, home);
    }
}
