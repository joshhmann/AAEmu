namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// R1 static composer (M9 substrate slice R1): seeds a ground-truth theft
/// event, has the witness observe it, and walks a 2-hop retell chain through
/// the in-memory <see cref="RumorStore"/>.
///
/// Composition only: drives the store's public API (<see cref="RumorStore.Publish"/>,
/// <see cref="RumorStore.Observe"/>, <see cref="RumorStore.TickRetell"/>) —
/// no tick subscription, no chat, no persistence, no engine path.
/// </summary>
public static class RumorComposer
{
    /// <summary>Library key for the scenario.</summary>
    public const string ScenarioName = "r1-rumor-propagation";

    /// <summary>Seed parameters for the ground-truth theft event.</summary>
    public sealed record TheftSeedOptions
    {
        /// <summary>Zone the theft happened in (retained verbatim at every hop).</summary>
        public string ZoneName { get; init; } = "Marianople";

        /// <summary>Ground-truth actor name (witness copy only, dropped on retell).</summary>
        public string ActorName { get; init; } = "Redhand";

        /// <summary>Subject item template id (retained verbatim at every hop).</summary>
        public uint ItemTemplateId { get; init; } = 15659;

        /// <summary>Ground-truth count (exact on witness copy, drifted on retell).</summary>
        public int Count { get; init; } = 5;

        /// <summary>Originating witness bot id.</summary>
        public uint WitnessId { get; init; } = 101;

        /// <summary>Game-loop tick the event is published on.</summary>
        public long Tick { get; init; } = 1000;
    }

    /// <summary>
    /// Seeds a ground-truth theft event and records the witness's exact copy.
    /// Returns the event id.
    /// </summary>
    public static Guid SeedTheftWithWitness(RumorStore store, TheftSeedOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        options ??= new TheftSeedOptions();
        var eventId = store.Publish(
            RumorKind.Theft,
            options.ZoneName,
            options.ActorName,
            options.ItemTemplateId,
            options.Count,
            options.WitnessId,
            options.Tick);
        store.Observe(eventId, options.WitnessId);
        return eventId;
    }

    /// <summary>
    /// Walks a retell chain: witness → listener[0] → listener[1] → … Stops at
    /// the first refused retell (hop cap) and returns the accepted copies.
    /// </summary>
    public static IReadOnlyList<RumorHearsay> RetellChain(RumorStore store, Guid eventId, uint witnessId, params uint[] listenerIds)
    {
        ArgumentNullException.ThrowIfNull(store);
        var copies = new List<RumorHearsay>();
        var from = witnessId;
        foreach (var to in listenerIds)
        {
            var result = store.TickRetell(eventId, from, to);
            if (!result.Accepted || result.Hearsay is null)
            {
                break;
            }
            copies.Add(result.Hearsay);
            from = to;
        }
        return copies;
    }
}
