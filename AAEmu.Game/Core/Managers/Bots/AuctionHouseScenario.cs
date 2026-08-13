using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Units;

using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Lane D auction-house conservation scenario (first consumer, t_52b2b084) —
/// the canonical no-Josh loop proof: a scripted FLEET of actors runs the
/// auction-house mechanic through the IGameplayActor CONTRACT ACTIONS ONLY
/// (PostAuction / BuyAuction), and the run asserts CONSERVATION from the
/// trace records + server state:
///
///   - items: every seeded item instance is accounted exactly once across
///     (auction lots ∪ mail attachments) — no duplication, no loss;
///   - currency: Σ(actor money snapshots + fleet mail money) == seed −
///     listing fees − 10% AH cut (the engine's documented sinks, ROADMAP M8
///     economic audit);
///   - lifecycle: every action's audit record carries the full
///     Requested → Accepted → Running → Completed transition set.
///
/// DENSITY-SAFE BY DESIGN: actors are provisioned and driven SEQUENTIALLY
/// (provision → rig → post → buy → snapshot → deactivate), so peak embodied
/// stays at ONE fleet actor + the bridge's primary — the same footprint the
/// existing quest templates have on any gate stage. The fleet scales via
/// AUCTION_FLEET_SIZE (default 25 = Stage25, the highest green density with
/// H2 landed; 50 = Stage50, the ≥6h soak gate — recorded as the ceiling).
///
/// The runner provisions extras server-side through the SAME machinery the
/// bridge scenario cmd uses (HeadlessSession.Provision), rigs each actor
/// through ordinary character-record fields + the normal acquisition path,
/// and drives ONLY contract actions. No new engine behavior; no direct DB
/// writes; no GM paths (target-lock: scenario DRIVES existing contract
/// actions only).
/// </summary>
public static class AuctionHouseScenario
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Library key (registered in <see cref="BotScenarioTemplates"/>).
    /// Kept short: the gate harness derives the scenario bot name as
    /// "tpl" + name minus dashes, and NameManager caps names at 18 chars.</summary>
    public const string ScenarioName = "ah-conservation";

    /// <summary>Item template each actor lists. MUST exist in the canonical
    /// compact.sqlite3 (10000 = craft scroll: price 100, refund 50, sellable).</summary>
    public const uint AuctionItemTemplateId = 10_000;

    /// <summary>Seed currency per actor (copper; 1 gold = 10,000 copper).</summary>
    public const int SeedMoney = 10_000;

    /// <summary>Listing terms: start 100c, buyout 10s, 6h (fee = buyout×1%×1 = 10c).</summary>
    public const int StartPrice = 100;
    public const int BuyoutPrice = 1_000;
    public const AuctionDuration Duration = AuctionDuration.AuctionDuration6Hours;

    /// <summary>
    /// Scenario parameters (live defaults above; unit rigs inject rig-seeded
    /// item templates so the fixture ItemManager resolves them).
    /// </summary>
    public sealed record AuctionScenarioOptions(
        uint ItemTemplateId = AuctionItemTemplateId,
        int SeedMoney = SeedMoney,
        int StartPrice = StartPrice,
        int BuyoutPrice = BuyoutPrice,
        AuctionDuration Duration = Duration,
        int? FleetSize = null);

    /// <summary>Fleet size: options override → AUCTION_FLEET_SIZE env → default 25
    /// (highest green stage with H2 landed; 50 = soak-gated ceiling).</summary>
    public static int ResolveFleetSize(AuctionScenarioOptions options)
    {
        if (options.FleetSize is { } explicitSize)
            return explicitSize;
        var raw = Environment.GetEnvironmentVariable("AUCTION_FLEET_SIZE");
        return int.TryParse(raw, out var v) && v >= 2 ? v : 25;
    }

    // ------------------------------------------------------------------ run

    /// <summary>
    /// Provisioner seam: index → embodied character for fleet members 2..N.
    /// The live path provisions through HeadlessSession.Provision (the
    /// bridge's own machinery); unit rigs inject fixture actors.
    /// </summary>
    public delegate Character? FleetProvisioner(int index, string botName);

    /// <summary>Live fleet provisioner (bridge-style managed accounts).</summary>
    public static Character? ProvisionLive(int index, string botName)
    {
        var username = BotAccountProvisioningService.ManagedUsernamePrefix + botName.ToLowerInvariant();
        var session = HeadlessSession.Provision(username, botName, Race.Nuian, Gender.Male, 1);
        return session.Character;
    }

    /// <summary>
    /// Runs the fleet auction-house conservation scenario. <paramref name="primary"/>
    /// is the bridge-provisioned actor (fleet member 1); members 2..N are
    /// provisioned sequentially, each driven through post + buy, snapshotted,
    /// then deactivated — peak embodied stays at ONE. Conservation + lifecycle
    /// are asserted from the global auction/mail state + money snapshots.
    /// </summary>
    public static BotScenarioRunner.ScenarioRunResult Run(Character primary)
        => Run(primary, ProvisionLive, new AuctionScenarioOptions());

    /// <summary>Testable core: inject a fleet provisioner (fixture rigs use
    /// GameplayActorTestRig-style actors; the live path uses
    /// <see cref="ProvisionLive"/>).</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(Character primary, FleetProvisioner provisioner)
        => Run(primary, provisioner, new AuctionScenarioOptions());

    /// <summary>Testable core with explicit scenario parameters.</summary>
    public static BotScenarioRunner.ScenarioRunResult Run(
        Character primary, FleetProvisioner provisioner, AuctionScenarioOptions options)
    {
        ArgumentNullException.ThrowIfNull(primary);
        ArgumentNullException.ThrowIfNull(provisioner);
        ArgumentNullException.ThrowIfNull(options);

        var fleetSize = ResolveFleetSize(options);
        var itemTemplateId = options.ItemTemplateId;
        var seedMoney = options.SeedMoney;
        var startPrice = options.StartPrice;
        var buyoutPrice = options.BuyoutPrice;
        var duration = options.Duration;
        var rigNotes = new List<string>();
        var stages = new List<BotScenarioRunner.ScenarioStageVerdict>();
        var criteria = new List<BotScenarioRunner.CriterionVerdict>();
        var traceRecords = new List<ActorAuditRecord>();
        var moneySnapshots = new List<long>();

        // Fleet roster: (characterId, botName) for conservation reads.
        var roster = new List<(uint CharacterId, string BotName)>();
        var deactivated = new List<Character>();

        try
        {
            if (fleetSize < 2)
                return Fail($"fleet size must be ≥ 2 for a post/buy ring (got {fleetSize})",
                    rigNotes, stages, criteria, traceRecords);

            // Fleet member 1 = the bridge-provisioned primary. If its bag is
            // empty (a prior run's row was adopted), rig still lands the item
            // fresh — rigging is idempotent per actor.
            roster.Add((primary.Id, primary.Name));

            // ------------------------------------------------ 2. DRIVE
            // Ring: actor i posts lot i; actor i+1 buys lot i (mod N); actor
            // 1 (the primary, alive throughout) buys lot N last — every lot
            // sold, every actor posts exactly one and buys exactly one. All
            // through the IGameplayActor contract actions with idempotency
            // keys (M5 retry safety).
            var lotIds = new ulong[fleetSize];
            var actors = new List<GameplayActor>();
            var primaryItemId = 0UL;
            for (var i = 0; i < fleetSize; i++)
            {
                Character character = primary;
                if (i > 0)
                {
                    var botName = $"ah{NewRunNonce()}{i:D2}";
                    character = provisioner(i, botName);
                    if (character == null)
                        return Fail($"actor {i + 1} provisioning failed", rigNotes, stages, criteria, traceRecords);
                    roster.Add((character.Id, botName));
                    deactivated.Add(character);
                }

                // Rig this actor (fresh money + item through normal surfaces).
                var rig = RigActor(character, i + 1, rigNotes, itemTemplateId, seedMoney);
                if (!rig)
                    return Fail($"actor {i + 1} rig incomplete", rigNotes, stages, criteria, traceRecords);

                var actor = new GameplayActor(character);
                actors.Add(actor);

                // POST — the actor's own item goes to the auction house.
                var itemId = character.Inventory.Bag
                    .GetAllItemsByTemplate(itemTemplateId, -1, out var items, out _) && items.Count > 0
                    ? items[0].Id
                    : 0UL;
                var post = actor.PostAuction(itemId, startPrice, buyoutPrice, duration,
                    idempotencyKey: $"ah-post-{i + 1}");
                if (i == 0)
                    primaryItemId = itemId;
                traceRecords.Add(actor.AuditTrace.Last());
                stages.Add(Stage("POST", i + 1, post));
                if (post.State != ActorLifecycleState.Completed)
                    return Fail($"actor {i + 1} PostAuction not Completed: {post.State} ({post.Failure}): {post.Detail}",
                        rigNotes, stages, criteria, traceRecords);
                lotIds[i] = Convert.ToUInt64(post.Result ?? 0UL);

                // BUY — the PREVIOUS member's lot (posted in the prior
                // iteration); actor 1's buy of lot N is the ring closure,
                // executed after the loop (lot N does not exist yet here).
                if (i > 0)
                {
                    var buy = actor.BuyAuction(lotIds[i - 1], buyoutPrice, idempotencyKey: $"ah-buy-{i + 1}");
                    traceRecords.Add(actor.AuditTrace.Last());
                    stages.Add(Stage("BUY", i + 1, buy));
                    if (buy.State != ActorLifecycleState.Completed)
                        return Fail($"actor {i + 1} BuyAuction not Completed: {buy.State} ({buy.Failure}): {buy.Detail}",
                            rigNotes, stages, criteria, traceRecords);
                }

                // Snapshot money BEFORE deactivation (the row saves on
                // deactivate). Actor 1's snapshot is taken after its closure
                // buy below.
                if (i > 0)
                    moneySnapshots.Add(character.Money);

                // Sequential deactivation keeps peak embodied at ONE fleet actor.
                if (i > 0)
                {
                    try
                    {
                        CharacterLifecycleService.Instance.Deactivate(character, CharacterLifecycleReason.Logout);
                        deactivated.Remove(character);
                    }
                    catch (Exception ex)
                    {
                        Logger.Debug(ex, "auction scenario: deactivate {Name} failed (best-effort)", character.Name);
                    }
                }
            }

            // Ring closure: actor 1 buys lot N (the last posted lot).
            var closure = actors[0].BuyAuction(lotIds[fleetSize - 1], buyoutPrice, idempotencyKey: "ah-buy-1");
            traceRecords.Add(actors[0].AuditTrace.Last());
            stages.Add(Stage("BUY", 1, closure));
            if (closure.State != ActorLifecycleState.Completed)
                return Fail($"actor 1 (ring closure) BuyAuction not Completed: {closure.State} ({closure.Failure}): {closure.Detail}",
                    rigNotes, stages, criteria, traceRecords);
            moneySnapshots.Add(primary.Money);

            // ------------------------------------- 3. IDEMPOTENCY PROBE
            // Re-issue actor 1's post with the SAME key — the ledger must
            // refuse it pre-flight (no double listing, no double fee).
            var dedupeActor = actors[0];
            var dedupe = dedupeActor.PostAuction(primaryItemId, startPrice, buyoutPrice, duration,
                idempotencyKey: "ah-post-1");
            traceRecords.Add(dedupeActor.AuditTrace.Last());
            var dedupeRefused = dedupe.State == ActorLifecycleState.Rejected &&
                                (dedupe.IsDedupeRejection || dedupe.Detail?.Contains("duplicate", StringComparison.OrdinalIgnoreCase) == true);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("idempotency-same-key-refused",
                dedupeRefused, dedupeRefused ? "same-key retry refused by ledger (no double listing)" : $"retry NOT refused: {dedupe.State} {dedupe.Detail}"));

            // ---------------------------------------------- 4. CONSERVE
            var itemVerdict = AssertItemConservation(roster, itemTemplateId, out var itemDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("item-conservation", itemVerdict, itemDetail));

            var currencyVerdict = AssertCurrencyConservation(moneySnapshots, roster, fleetSize, seedMoney, buyoutPrice, duration, out var currencyDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("currency-conservation", currencyVerdict, currencyDetail));

            var traceVerdict = AssertTraceCompleteness(traceRecords, fleetSize, out var traceDetail);
            criteria.Add(new BotScenarioRunner.CriterionVerdict("trace-complete", traceVerdict, traceDetail));

            var passed = criteria.All(c => c.Passed);
            return new BotScenarioRunner.ScenarioRunResult
            {
                Template = ScenarioName,
                Passed = passed,
                FailStage = passed ? "" : "VERIFY",
                RigNotes = rigNotes,
                Gates = [],
                Stages = stages,
                Criteria = criteria,
                ActorRequests = traceRecords.Count
            };
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "auction-house scenario crashed");
            return Fail($"RUN crashed: {ex.GetType().Name}: {ex.Message}", rigNotes, stages, criteria, traceRecords);
        }
        finally
        {
            // Best-effort cleanup of anything still embodied.
            foreach (var c in deactivated)
            {
                try
                {
                    CharacterLifecycleService.Instance.Deactivate(c, CharacterLifecycleReason.Logout);
                }
                catch (Exception ex)
                {
                    Logger.Debug(ex, "auction scenario cleanup: deactivate {Name} failed", c.Name);
                }
            }
        }
    }

    // ------------------------------------------------------------- helpers

    private static string NewRunNonce()
        => Guid.NewGuid().ToString("N")[..6];

    /// <summary>
    /// Rigs one actor: money set on the ordinary character record (the same
    /// class of field the template rig sets for level), item through the
    /// normal acquisition path (AcquireDefaultItem). Returns false when the
    /// rig did not land (money refused / item absent).
    /// </summary>
    private static bool RigActor(Character actor, int index, List<string> rigNotes, uint itemTemplateId, int seedMoney)
    {
        try
        {
            actor.Money = seedMoney;
            var controller = new PlayerBotController(actor);
            controller.StockInventory(itemTemplateId, 1);
            var inBag = actor.Inventory.Bag
                .GetAllItemsByTemplate(itemTemplateId, -1, out var items, out _) && items.Count > 0;
            if (!inBag)
            {
                rigNotes.Add($"actor {index} item seed did not land in bag (template {itemTemplateId})");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            rigNotes.Add($"actor {index} rig crashed: {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    private static BotScenarioRunner.ScenarioStageVerdict Stage(string kind, int index, ActorRequest request)
        => new($"{kind}-{index}", 0, request.State.ToString(), request.Result?.ToString() ?? "", request.Detail ?? "");

    // ------------------------------------------------------------- asserts

    /// <summary>
    /// Item conservation: every seeded item instance (one per actor) must be
    /// reachable exactly once across (auction lots ∪ mail attachments) —
    /// the global view survives actor deactivation. A missing or duplicated
    /// instance is conservation failure (M8 audit semantics).
    /// </summary>
    private static bool AssertItemConservation(List<(uint CharacterId, string BotName)> roster, uint itemTemplateId, out string detail)
    {
        var seeded = roster.Count;
        var found = new HashSet<ulong>();
        var duplicates = new List<ulong>();

        void Account(IEnumerable<Item> items)
        {
            foreach (var item in items)
            {
                if (item.TemplateId != itemTemplateId)
                    continue;
                if (!found.Add(item.Id))
                    duplicates.Add(item.Id);
            }
        }

        foreach (var lot in AuctionManager.Instance.AuctionLots.Values)
            if (lot.Item != null)
                Account([lot.Item]);

        foreach (var (characterId, _) in roster)
        {
            foreach (var mail in MailManager.Instance.GetCurrentMailList(characterId).Values)
                Account(mail.Body.Attachments);
        }

        detail = $"seeded={seeded} accounted={found.Count} duplicates={duplicates.Count} lotsRemaining={AuctionManager.Instance.AuctionLots.Count}";
        return found.Count == seeded && duplicates.Count == 0;
    }

    /// <summary>
    /// Currency conservation: Σ(actor money snapshots) + Σ(mail CopperCoins
    /// over the fleet's mail) == N×seed − N×listingFee − N×0.1×buyout. The
    /// listing fee (buyout×1%×(duration+1), capped at 100g) and the 10% AH
    /// cut per sale are the engine's documented sinks; anything else missing
    /// is duplication or leakage. The seller's 90% share lands in MAIL money
    /// (RemoveAuctionLotSold → sellMail.AttachMoney), so both surfaces must
    /// be summed.
    /// </summary>
    private static bool AssertCurrencyConservation(
        List<long> moneySnapshots, List<(uint CharacterId, string BotName)> roster, int fleetSize,
        int seedMoney, int buyoutPrice, AuctionDuration duration, out string detail)
    {
        var actorMoney = moneySnapshots.Sum();
        var mailMoney = 0L;
        foreach (var (characterId, _) in roster)
        {
            foreach (var mail in MailManager.Instance.GetCurrentMailList(characterId).Values)
                mailMoney += mail.Body.CopperCoins;
        }

        var expected = (long)fleetSize * seedMoney
                       - (long)fleetSize * ListingFee(buyoutPrice, duration)
                       - (long)fleetSize * (long)(buyoutPrice * 0.1);

        detail = $"actorMoney={actorMoney} mailMoney={mailMoney} total={actorMoney + mailMoney} expected={expected}";
        return actorMoney + mailMoney == expected;
    }

    /// <summary>The engine's listing-fee formula (PostLotOnAuction), capped.</summary>
    private static int ListingFee(int buyout, AuctionDuration duration)
    {
        var fee = (int)(buyout * 0.01 * ((int)duration + 1));
        return fee > GameplayActor.MaxListingFee ? GameplayActor.MaxListingFee : fee;
    }

    /// <summary>
    /// Lifecycle correctness: every COMPLETED action's trace record must
    /// carry the full Requested → Accepted → Running → Completed transition
    /// set; the fleet must produce ≥ 2N completed records (N posts + N buys);
    /// and the dedupe probe's Rejected record must NOT carry Running (a
    /// refusal is not an execution).
    /// </summary>
    private static bool AssertTraceCompleteness(List<ActorAuditRecord> records, int fleetSize, out string detail)
    {
        var completed = records.Where(r => r.Result == ActorLifecycleState.Completed).ToList();
        var incomplete = completed
            .Where(r => r.StateChanges.Count == 0 ||
                        !r.StateChanges.Any(s => s.Contains("Requested")) ||
                        !r.StateChanges.Any(s => s.Contains("Accepted")) ||
                        !r.StateChanges.Any(s => s.Contains("Running")) ||
                        !r.StateChanges.Any(s => s.Contains("Completed")))
            .ToList();
        var rejectedRunning = records
            .Where(r => r.Result == ActorLifecycleState.Rejected && r.StateChanges.Any(s => s.Contains("Running")))
            .ToList();

        detail = $"records={records.Count} completed={completed.Count} (expected ≥ {2 * fleetSize}) " +
                 $"incompleteCompleted={incomplete.Count} rejectedWithRunning={rejectedRunning.Count}";
        return completed.Count >= 2 * fleetSize && incomplete.Count == 0 && rejectedRunning.Count == 0;
    }

    private static BotScenarioRunner.ScenarioRunResult Fail(
        string reason, List<string> rigNotes,
        List<BotScenarioRunner.ScenarioStageVerdict> stages,
        List<BotScenarioRunner.CriterionVerdict> criteria,
        List<ActorAuditRecord> traceRecords)
        => new()
        {
            Template = ScenarioName,
            Passed = false,
            FailStage = "RUN",
            FailReason = reason,
            RigNotes = rigNotes,
            Gates = [],
            Stages = stages,
            Criteria = criteria,
            ActorRequests = traceRecords.Count
        };
}
