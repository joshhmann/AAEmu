using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// R1 rumor kinds (M9 substrate slice R1). The kind drives the retell noun
/// only — propagation, decay, and degradation are kind-agnostic.
/// </summary>
public enum RumorKind
{
    Theft,
    Murder,
    PriceShift,
    Sighting
}

/// <summary>
/// The noisy consumer copy of a rumor. This is the ONLY rumor payload any
/// consumer ever reads: zone name retained, actor name dropped after hop 1,
/// counts drifted ±1 per hop, confidence decayed. Ground truth is never
/// exposed — the degradation rule IS the feature (imperfect information).
/// </summary>
public sealed class RumorHearsay
{
    /// <summary>The event this copy descends from.</summary>
    public required Guid EventId { get; init; }

    /// <summary>Rumor kind.</summary>
    public required RumorKind Kind { get; init; }

    /// <summary>Zone name — retained verbatim at every hop.</summary>
    public string ZoneName { get; init; } = string.Empty;

    /// <summary>
    /// Actor name — intact on the witness copy (hop 0), dropped (empty) on
    /// every retell (hop 1+).
    /// </summary>
    public string ActorName { get; init; } = string.Empty;

    /// <summary>Subject item template id — retained verbatim at every hop.</summary>
    public uint ItemTemplateId { get; init; }

    /// <summary>Subject count — exact on the witness copy, drifted ±1 per hop.</summary>
    public int Count { get; init; }

    /// <summary>Confidence — 1.0 on the witness copy, ×<see cref="RumorStore.DecayPerHop"/> per hop.</summary>
    public double Confidence { get; init; }

    /// <summary>Retell distance from the witness (witness copy = 0).</summary>
    public int Hop { get; init; }

    /// <summary>Game-loop tick the ground-truth event was published on.</summary>
    public long Tick { get; init; }

    /// <summary>Deterministic retell phrasing built from the degraded copy.</summary>
    public string RetellText { get; init; } = string.Empty;

    /// <summary>Wall-clock stamp of when this copy was taken (never rendered into text).</summary>
    public DateTimeOffset ObservedAt { get; init; }
}

/// <summary>
/// Fail-closed retell outcome. A refused retell stores no copy and leaves
/// existing copies untouched.
/// </summary>
public sealed class RumorRetellResult
{
    /// <summary>True when the retell was accepted and <see cref="Hearsay"/> holds the new copy.</summary>
    public bool Accepted { get; init; }

    /// <summary>The new degraded copy (null when refused).</summary>
    public RumorHearsay? Hearsay { get; init; }

    /// <summary>Accept detail or refusal reason (never empty on refusal).</summary>
    public string Reason { get; init; } = string.Empty;
}

/// <summary>
/// R1 in-memory rumor graph (M9 substrate slice R1): event → witness edge →
/// hearsay edges with per-hop decay and degradation.
///
/// Slice boundaries (hard): NO table, NO tick wiring, NO chat emission, NO
/// LLM, no <c>ChatManager</c>/<c>BotChatterService</c>/<c>PlayerBotMetadataStore</c>/
/// <c>SaveManager</c> touches. This is a pure in-memory graph with injected
/// personality + <see cref="TimeProvider"/> and zero singleton lookups —
/// inert unless a caller drives <see cref="Publish"/>, <see cref="Observe"/>,
/// or <see cref="TickRetell"/>.
///
/// Imperfect-information rule: the ground-truth payload lives in a private
/// nested record with no public accessor; consumers read only
/// <see cref="RumorHearsay"/> via <see cref="TryGetHearsay"/>.
/// </summary>
public sealed class RumorStore
{
    /// <summary>Confidence multiplier applied per retell hop (pinned decay bound).</summary>
    public const double DecayPerHop = 0.7;

    /// <summary>
    /// Retell attempts reaching this hop are refused fail-closed with a
    /// reason. Witness copy = hop 0, so hops 1..3 propagate, hop 4+ refuses.
    /// </summary>
    public const int MaxRetellHop = 4;

    private static readonly string[] s_voices =
    [
        "lawful", "greedy", "cheerful", "paranoid", "pirate", "farmer", "merchant", "guard"
    ];

    /// <summary>Ground truth — private by design, never exposed to consumers.</summary>
    private sealed class RumorGroundTruth
    {
        public required Guid EventId { get; init; }
        public required RumorKind Kind { get; init; }
        public string ZoneName { get; init; } = string.Empty;
        public string ActorName { get; init; } = string.Empty;
        public uint ItemTemplateId { get; init; }
        public int Count { get; init; }
        public uint WitnessId { get; init; }
        public long Tick { get; init; }
    }

    private readonly Lock _gate = new();
    private readonly Func<uint, PlayerBotMetadata> _personalityProvider;
    private readonly TimeProvider _timeProvider;
    private readonly Dictionary<Guid, RumorGroundTruth> _truths = new();
    private readonly Dictionary<(Guid EventId, uint BotId), RumorHearsay> _copies = new();

    /// <summary>
    /// Creates an empty rumor graph.
    /// </summary>
    /// <param name="personalityProvider">Resolves bot metadata for speaker voice flavor (defaults to empty metadata).</param>
    /// <param name="timeProvider">Clocks copy stamps (defaults to system; never affects retell text).</param>
    public RumorStore(Func<uint, PlayerBotMetadata>? personalityProvider = null, TimeProvider? timeProvider = null)
    {
        _personalityProvider = personalityProvider ?? (static id => PlayerBotMetadata.Empty(id));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <summary>Number of published ground-truth events.</summary>
    public int EventCount
    {
        get
        {
            lock (_gate)
            {
                return _truths.Count;
            }
        }
    }

    /// <summary>
    /// Publishes a ground-truth event. Returns the event id. Ground truth is
    /// stored privately — no accessor ever returns it.
    /// </summary>
    public Guid Publish(RumorKind kind, string zoneName, string actorName, uint itemTemplateId, int count, uint witnessId, long tick)
    {
        var truth = new RumorGroundTruth
        {
            EventId = Guid.NewGuid(),
            Kind = kind,
            ZoneName = zoneName,
            ActorName = actorName,
            ItemTemplateId = itemTemplateId,
            Count = count,
            WitnessId = witnessId,
            Tick = tick
        };
        lock (_gate)
        {
            _truths[truth.EventId] = truth;
        }
        return truth.EventId;
    }

    /// <summary>
    /// Records the originating witness's exact copy (hop 0, confidence 1.0,
    /// zone + template + actor + count intact). Returns null for unknown
    /// events or when the caller is not the recorded witness — only the
    /// witness ever holds the exact copy.
    /// </summary>
    public RumorHearsay? Observe(Guid eventId, uint witnessId)
    {
        lock (_gate)
        {
            if (!_truths.TryGetValue(eventId, out var truth) || truth.WitnessId != witnessId)
            {
                return null;
            }
            var copy = new RumorHearsay
            {
                EventId = eventId,
                Kind = truth.Kind,
                ZoneName = truth.ZoneName,
                ActorName = truth.ActorName,
                ItemTemplateId = truth.ItemTemplateId,
                Count = truth.Count,
                Confidence = 1.0,
                Hop = 0,
                Tick = truth.Tick,
                RetellText = BuildRetellText(ResolveVoice(witnessId), truth.Kind, truth.ZoneName, truth.Count, 0),
                ObservedAt = _timeProvider.GetUtcNow()
            };
            _copies[(eventId, witnessId)] = copy;
            return copy;
        }
    }

    /// <summary>
    /// Retells an event from one bot to another. The new copy degrades:
    /// actor name dropped, count drifted ±1 (deterministic), confidence
    /// decayed ×<see cref="DecayPerHop"/>. Hop 4+ attempts are refused
    /// fail-closed with a reason and store nothing.
    /// </summary>
    public RumorRetellResult TickRetell(Guid eventId, uint fromBotId, uint toBotId)
    {
        lock (_gate)
        {
            if (!_truths.TryGetValue(eventId, out var truth))
            {
                return new RumorRetellResult { Accepted = false, Reason = "unknown event" };
            }
            if (!_copies.TryGetValue((eventId, fromBotId), out var source))
            {
                return new RumorRetellResult { Accepted = false, Reason = "source holds no copy" };
            }
            var hop = source.Hop + 1;
            if (hop >= MaxRetellHop)
            {
                return new RumorRetellResult { Accepted = false, Reason = $"retell hop {hop} reaches cap {MaxRetellHop}" };
            }
            // Degradation is seeded from the event CONTENT (kind/zone/truth),
            // never the random event id — identical seeds retell identically.
            var driftedCount = DegradeCount(truth.Count, truth.Kind, truth.ZoneName, hop);
            var copy = new RumorHearsay
            {
                EventId = eventId,
                Kind = truth.Kind,
                ZoneName = truth.ZoneName,
                ActorName = string.Empty,
                ItemTemplateId = truth.ItemTemplateId,
                Count = driftedCount,
                Confidence = source.Confidence * DecayPerHop,
                Hop = hop,
                Tick = truth.Tick,
                RetellText = BuildRetellText(ResolveVoice(fromBotId), truth.Kind, truth.ZoneName, driftedCount, hop),
                ObservedAt = _timeProvider.GetUtcNow()
            };
            _copies[(eventId, toBotId)] = copy;
            return new RumorRetellResult { Accepted = true, Hearsay = copy, Reason = $"retell hop {hop}" };
        }
    }

    /// <summary>
    /// Reads the noisy copy addressed to a bot. Never returns ground truth:
    /// witness callers get their exact hop-0 copy, everyone else only a
    /// degraded retell copy (or false when the bot holds nothing).
    /// </summary>
    public bool TryGetHearsay(Guid eventId, uint botId, out RumorHearsay? hearsay)
    {
        lock (_gate)
        {
            return _copies.TryGetValue((eventId, botId), out hearsay);
        }
    }

    private string ResolveVoice(uint speakerId)
    {
        PlayerBotMetadata metadata;
        try
        {
            metadata = _personalityProvider(speakerId);
        }
        catch
        {
            return "word";
        }
        var personality = metadata?.Personality ?? string.Empty;
        foreach (var voice in s_voices)
        {
            if (personality.Contains(voice, StringComparison.OrdinalIgnoreCase))
            {
                return voice;
            }
        }
        return "word";
    }

    private static int DegradeCount(int truth, RumorKind kind, string zone, int hop)
    {
        uint hash = 2166136261u;
        hash ^= (uint)kind;
        hash *= 16777619u;
        foreach (var c in zone)
        {
            hash ^= c;
            hash *= 16777619u;
        }
        hash ^= (uint)hop;
        hash *= 16777619u;
        var delta = (hash & 1) == 0 ? 1 : -1;
        return Math.Max(0, truth + delta);
    }

    private static string BuildRetellText(string voice, RumorKind kind, string zone, int count, int hop)
        => $"[{voice}] {KindNoun(kind)} in {zone}: {count} crates missing (hop {hop})";

    private static string KindNoun(RumorKind kind)
        => kind switch
        {
            RumorKind.Theft => "theft",
            RumorKind.Murder => "murder",
            RumorKind.PriceShift => "price shift",
            RumorKind.Sighting => "sighting",
            _ => "trouble"
        };
}
