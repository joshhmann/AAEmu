using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Pre-LLM deterministic bot chatter (ROADMAP M8.5a "lightweight social" +
/// chatter tiering, Layer 1 templates + Layer 2 procedural fill). When a
/// character (human or bot) comes within <see cref="BotChatterOptions.GreetingRadius"/>
/// of an embodied bot that has been quiet long enough, the bot emits ONE
/// canned greeting through the real local-chat path (<see cref="LocalChatChatterSink"/>).
///
/// Hard budgets (ROADMAP requirements), all enforced BEFORE any send:
///   - per-bot cooldown between ANY two lines (<see cref="BotChatterOptions.PerBotCooldown"/>, ≥90s);
///   - per-zone message budget per minute (<see cref="BotChatterOptions.ZoneMessagesPerMinute"/>);
///   - NEVER during combat — a scan skips bots and targets with IsInBattle;
///   - pair cooldown so the same two characters don't re-greet in a loop.
///
/// Deterministic by construction (no wall-clock randomness): archetype and
/// line selection are seeded from stable ids via <see cref="BotChatterTemplates"/>.
///
/// Fail-safe contract: no exception from the manager enumeration or the chat
/// sink ever propagates into gameplay paths — the first failure of an episode
/// is logged once and chatter stays silent for the remainder of that tick.
///
/// Disabled by default ("Bots"."EnableChatter" / AAEMU_BOT_CHATTER_ENABLED);
/// <see cref="Start"/> is a strict no-op while the gate is off.
/// </summary>
public sealed class BotChatterService
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private readonly IPlayerBotManager _manager;
    private readonly BotChatterOptions _options;
    private readonly IBotChatterSink _sink;
    private readonly Func<Character, IReadOnlyList<Character>> _nearbyResolver;
    private readonly Func<uint, string> _zoneNameResolver;
    private readonly Func<uint, string> _personalityResolver;
    private readonly ITickManager _tickManager;
    private readonly TimeProvider _timeProvider;

    // Budget/cooldown state. Mutated only on the execution boundary (the
    // game-loop tick) in production; the lock also covers direct test pumps.
    private readonly object _stateLock = new();
    private readonly Dictionary<uint, DateTime> _lastSpokeUtc = [];
    private readonly Dictionary<(uint BotId, uint TargetId), DateTime> _lastPairGreetUtc = [];
    private readonly Dictionary<uint, (DateTime WindowStart, int Sent)> _zoneBudget = [];

    private int _started;
    private bool _failureLoggedThisEpisode;
    private long _totalLinesSent;

    public BotChatterService(
        IPlayerBotManager manager,
        BotChatterOptions? options = null,
        IBotChatterSink? sink = null,
        Func<Character, IReadOnlyList<Character>>? nearbyResolver = null,
        Func<uint, string>? zoneNameResolver = null,
        Func<uint, string>? personalityResolver = null,
        ITickManager? tickManager = null,
        TimeProvider? timeProvider = null)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _options = options ?? new BotChatterOptions();
        _sink = sink ?? new LocalChatChatterSink();
        _nearbyResolver = nearbyResolver ?? DefaultNearbyResolver;
        _zoneNameResolver = zoneNameResolver ?? DefaultZoneNameResolver;
        _personalityResolver = personalityResolver ?? DefaultPersonalityResolver;
        _tickManager = tickManager ?? TickManager.Instance;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>True between a successful gated <see cref="Start"/> and <see cref="StopAsync"/>.</summary>
    public bool IsRunning => Volatile.Read(ref _started) == 1;

    /// <summary>Cumulative lines actually emitted through the sink (diagnostics).</summary>
    public long TotalLinesSent => Volatile.Read(ref _totalLinesSent);

    /// <summary>The resolved options snapshot.</summary>
    public BotChatterOptions Options => _options;

    /// <summary>
    /// Subscribes the proximity scan to the game-loop tick (inline, useAsync:
    /// false — the same seam PlayerBotScheduler uses). A strict no-op while
    /// the feature gate is off; safe to call repeatedly.
    /// </summary>
    /// <returns>True when the service is now running; false when disabled.</returns>
    public bool Start()
    {
        if (!_options.Enabled)
        {
            Logger.Debug("BotChatterService disabled (Bots.EnableChatter / AAEMU_BOT_CHATTER_ENABLED unset) — inert");
            return false;
        }

        if (Interlocked.Exchange(ref _started, 1) != 0)
            return true;

        _tickManager.OnTick.Subscribe(ScanTick, _options.ScanInterval, useAsync: false, name: "BotChatterService.Scan");
        Logger.Info(
            "BotChatterService started: radius {Radius}m, per-bot cooldown {Cooldown}s, zone budget {Budget}/min",
            _options.GreetingRadius, _options.PerBotCooldown.TotalSeconds, _options.ZoneMessagesPerMinute);
        return true;
    }

    /// <summary>Unsubscribes the proximity scan. Safe when never started.</summary>
    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _started, 0) == 0)
            return Task.CompletedTask;

        try
        {
            _tickManager.OnTick.UnSubscribe(ScanTick);
        }
        catch (Exception ex)
        {
            Logger.Warn(ex, "BotChatterService: failed to unsubscribe scan tick");
        }

        return Task.CompletedTask;
    }

    /// <summary>Tick subscriber entry (game-loop thread).</summary>
    private void ScanTick(TimeSpan delta) => RunScan();

    /// <summary>
    /// One proximity scan pass. Internal so tests drive scans deterministically
    /// without starting the tick subscription.
    /// </summary>
    internal void RunScan()
    {
        // Enabled-gate only: production callers reach this through the
        // Start() tick subscription (which itself refuses to arm while
        // disabled), and test rigs pump scans directly.
        if (!_options.Enabled)
            return;

        try
        {
            ScanOnce();
            _failureLoggedThisEpisode = false; // clean pass ends the log-once episode
        }
        catch (Exception ex)
        {
            // Fail-safe: never throw into gameplay paths.
            LogFailureOnce(ex, "BotChatterService scan failed — chatter suppressed for this tick");
        }
    }

    private void ScanOnce()
    {
        var now = UtcNow;

        foreach (var runtime in _manager.GetActive())
        {
            if (runtime.State != PlayerBotState.Active)
                continue;

            var bot = runtime.Character;
            if (bot == null || bot.IsInBattle)
                continue;

            // Per-bot quiet window between ANY two lines (checked BEFORE the
            // proximity work — a quiet bot costs almost nothing).
            lock (_stateLock)
            {
                if (_lastSpokeUtc.TryGetValue(bot.Id, out var lastSpoke) &&
                    now - lastSpoke < _options.PerBotCooldown)
                    continue;
            }

            var candidates = _nearbyResolver(bot);
            Character? target = null;
            foreach (var candidate in candidates)
            {
                if (candidate == null || candidate.Id == bot.Id)
                    continue;
                if (candidate.IsInBattle)
                    continue; // NEVER chatter during combat

                lock (_stateLock)
                {
                    if (_lastPairGreetUtc.TryGetValue((bot.Id, candidate.Id), out var pairGreeted) &&
                        now - pairGreeted < _options.PairCooldown)
                        continue;
                }

                target = candidate;
                break;
            }

            if (target == null)
                continue;

            var zoneId = bot.Transform.ZoneId;

            // Zone budget BEFORE send (rate-limit enforced before send).
            lock (_stateLock)
            {
                if (_zoneBudget.TryGetValue(zoneId, out var window))
                {
                    if (now - window.WindowStart >= TimeSpan.FromMinutes(1))
                        _zoneBudget[zoneId] = (now, 0); // window rolled over
                    else if (window.Sent >= _options.ZoneMessagesPerMinute)
                        continue; // zone budget exhausted for this minute
                }
            }

            var archetype = BotChatterTemplates.ResolveArchetype(_personalityResolver(bot.Id), bot.Id);
            var line = BotChatterTemplates.Substitute(
                BotChatterTemplates.PickLine(archetype, bot.Id, target.Id),
                bot.Name, target.Name, _zoneNameResolver(zoneId));

            try
            {
                _sink.Say(bot, line);
            }
            catch (Exception ex)
            {
                // Send failure → chatter disabled for the REMAINDER of this
                // tick (logged once per failure episode, never thrown).
                LogFailureOnce(ex, "BotChatterService send failed for bot {CharacterId} — chatter suppressed for this tick", bot.Id);
                return;
            }

            // State advances ONLY after a successful send.
            lock (_stateLock)
            {
                _lastSpokeUtc[bot.Id] = now;
                _lastPairGreetUtc[(bot.Id, target.Id)] = now;
                if (_zoneBudget.TryGetValue(zoneId, out var window2) && now - window2.WindowStart < TimeSpan.FromMinutes(1))
                    _zoneBudget[zoneId] = (window2.WindowStart, window2.Sent + 1);
                else
                    _zoneBudget[zoneId] = (now, 1);
            }

            Interlocked.Increment(ref _totalLinesSent);

            // ONE line per bot per scan — a proximity event greets once.
        }
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    private void LogFailureOnce(Exception ex, string message, params object[] args)
    {
        if (_failureLoggedThisEpisode)
            return;
        _failureLoggedThisEpisode = true;
        Logger.Error(ex, message, args);
    }

    /// <summary>Default proximity source: the same region query GameplayActor uses.</summary>
    private IReadOnlyList<Character> DefaultNearbyResolver(Character bot)
        => WorldManager.GetAround<Character>(bot, _options.GreetingRadius);

    /// <summary>Default zone naming: real zone names from the loaded world data.</summary>
    private static string DefaultZoneNameResolver(uint zoneKey)
        => ZoneManager.Instance.GetZoneByKey(zoneKey)?.Name ?? $"zone {zoneKey}";

    /// <summary>
    /// Default personality source: the B4 metadata store's Personality field
    /// (empty metadata → deterministic fallback inside the template bank).
    /// The store never throws (DB-less reads return empty rows).
    /// </summary>
    private static string DefaultPersonalityResolver(uint characterId)
        => PlayerBotMetadataStore.Instance.GetForRead(characterId).Personality;
}
