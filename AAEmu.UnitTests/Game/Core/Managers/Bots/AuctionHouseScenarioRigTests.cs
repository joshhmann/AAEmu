using System.Numerics;
using System.Reflection;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Auction;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Mails;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Unit rigs for the Lane D auction-house conservation scenario
/// (t_52b2b084) — the scenario runner driven on the fixture rig:
///   - a full post/buy ring completes through the real engine trade paths;
///   - items are conserved (each seeded instance accounted exactly once);
///   - currency is conserved modulo the documented engine sinks (listing
///     fee + 10% AH cut);
///   - every action's trace record carries the full lifecycle transition set
///     and the same-key retry is refused (idempotency, no double listing).
/// </summary>
[NotInParallel]
public class AuctionHouseScenarioRigTests
{
    private const uint RigItemTemplateId = 88_103; // rig-seeded auction item (refund 25, sellable)
    private const int RigSeedMoney = 10_000;
    private const int RigBuyout = 1_000;

    private void SeedSurfaceAndReset()
    {
        GameplayActorTestRig.Seed(); // full bootstrap (DI + singletons + trade surface)
        AuctionManager.Instance.AuctionLots.Clear();
    }

    /// <summary>
    /// Replaces the rig's deliberately-failing MailManager/AuctionManager
    /// with working in-memory ones: the engine's auction sale path delivers
    /// the buyer item and the seller's 90% share through MAIL
    /// (RemoveAuctionLotSold), which resolves buyer ids through the auction
    /// manager's injected INameManager and receiver names through the real
    /// NameManager singleton. The rig's stock mocks (names resolve to null /
    /// id 0) would swallow both mails, so the conservation surface (mail
    /// attachments + mail money) would read empty. This rig resolves the
    /// fleet characters so the full engine mail path lands in-memory.
    /// </summary>
    private static void SeedWorkingAuctionMail(List<Character> fleet)
    {
        // The auction mail path resolves receiver names through the real
        // NameManager singleton (MailForAuction.FinalizeForSale*), which
        // NORMALIZES names to title case on AddCharacter. The injected
        // INameManager mocks must resolve the SAME normalized names or the
        // id/name verification in MailManager.Send / AuctionManager fails
        // (raw 'probe-seller' vs normalized 'Probe-seller' → id 0 → no mail).
        foreach (var c in fleet)
            NameManager.Instance.AddCharacter(c.Id, c.Name, c.AccountId);

        var nameMock = Mock.Of<INameManager>();
        foreach (var c in fleet)
        {
            var normalized = NameManager.Instance.GetCharacterName(c.Id) ?? c.Name;
            nameMock.GetCharacterName(c.Id).Returns(normalized);
            nameMock.GetCharacterId(normalized).Returns(c.Id);
            // The auction sale path passes the RAW character name
            // (player.Name) into AuctionManager.GetCharacterId — resolve it too.
            nameMock.GetCharacterId(c.Name).Returns(c.Id);
        }

        var mailId = 1u;
        var idMock = Mock.Of<IMailIdManager>();
        idMock.GetNextId().Returns(() => mailId++);

        var mailManager = new MailManager(
            idMock.Object,
            nameMock.Object,
            ItemManager.Instance,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<IWorldManager>().Object,
            new Lazy<IHousingManager>(() => Mock.Of<IHousingManager>().Object),
            Mock.Of<ILocalizationManager>().Object);
        mailManager._allPlayerMails = new Dictionary<long, BaseMail>();

        // The auction sale path resolves the BUYER's id through the auction
        // manager's injected INameManager (AuctionManager.cs:51) — the rig's
        // stock mock returns 0 and the buy mail never finalizes. Re-seed
        // with the working name mock + the rig's lot-id counter shape.
        var lotId = 100u;
        var auctionIdMock = Mock.Of<IAuctionIdManager>();
        auctionIdMock.GetNextId().Returns(() => lotId++);
        var auctionManager = new AuctionManager(
            ItemManager.Instance,
            nameMock.Object,
            auctionIdMock.Object,
            Mock.Of<ILocalizationManager>().Object,
            Mock.Of<ITaskManager>().Object);

        // Force-replace both singletons (the rig's seeds are missing-only;
        // the working surfaces must win for this suite).
        var mailField = typeof(Singleton<MailManager>).GetField("s_instance",
            BindingFlags.NonPublic | BindingFlags.Static);
        mailField?.SetValue(null, mailManager);
        var auctionField = typeof(Singleton<AuctionManager>).GetField("s_instance",
            BindingFlags.NonPublic | BindingFlags.Static);
        auctionField?.SetValue(null, auctionManager);
    }

    /// <summary>Fixtures: one primary + N-1 provisioned extras on the rig world.</summary>
    private static (Character Primary, List<Character> Fleet) RigFleet(int size)
    {
        var (_, session) = GameplayActorTestRig.CreateActor("ah-fleet-primary");
        var fleet = new List<Character> { session.Character };
        for (var i = 2; i <= size; i++)
        {
            var (_, extraSession) = GameplayActorTestRig.CreateActor($"ah-fleet-{i:D2}");
            fleet.Add(extraSession.Character);
        }
        return (session.Character, fleet);
    }

    /// <summary>Provisioner that hands out the pre-rigged fixture actors (index 0 = primary).</summary>
    private static AuctionHouseScenario.FleetProvisioner FixtureProvisioner(List<Character> fleet)
        => (index, _) => index < fleet.Count ? fleet[index] : null;

    [Test]
    public async Task AuctionHouse_ConservationScenario_Passes_FullRing()
    {
        SeedSurfaceAndReset();
        var fleetSize = 4;
        var (primary, fleet) = RigFleet(fleetSize);
        SeedWorkingAuctionMail(fleet);

        var result = AuctionHouseScenario.Run(primary, FixtureProvisioner(fleet),
            new AuctionHouseScenario.AuctionScenarioOptions(
                ItemTemplateId: RigItemTemplateId,
                SeedMoney: RigSeedMoney,
                StartPrice: 100,
                BuyoutPrice: RigBuyout,
                Duration: AuctionDuration.AuctionDuration6Hours,
                FleetSize: fleetSize));

        await Assert.That(result.Passed).IsTrue().Because(result.Evidence());
        await Assert.That(result.Criteria.Any(c => c.Name == "item-conservation" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "currency-conservation" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "trace-complete" && c.Passed)).IsTrue();
        await Assert.That(result.Criteria.Any(c => c.Name == "idempotency-same-key-refused" && c.Passed)).IsTrue();
    }

    [Test]
    public async Task AuctionHouse_Conservation_ItemsConserved_NoDuplication()
    {
        SeedSurfaceAndReset();
        var fleetSize = 3;
        var (primary, fleet) = RigFleet(fleetSize);
        SeedWorkingAuctionMail(fleet);
        // Seed one extra item into the primary's bag that must NOT be swept
        // into the conservation surface (different template id).
        var (_, extraSession) = GameplayActorTestRig.CreateActor("ah-extra-bag");
        GameplayActorTestRig.StockItem(extraSession, 88_101, 1);

        var result = AuctionHouseScenario.Run(primary, FixtureProvisioner(fleet),
            new AuctionHouseScenario.AuctionScenarioOptions(
                ItemTemplateId: RigItemTemplateId,
                SeedMoney: RigSeedMoney,
                BuyoutPrice: RigBuyout,
                Duration: AuctionDuration.AuctionDuration6Hours,
                FleetSize: fleetSize));

        await Assert.That(result.Passed).IsTrue().Because(result.Evidence());
        var itemCriterion = result.Criteria.First(c => c.Name == "item-conservation");
        await Assert.That(itemCriterion.Passed).IsTrue();
        await Assert.That(itemCriterion.Detail).Contains("duplicates=0");
        await Assert.That(itemCriterion.Detail).Contains("accounted=3");
    }

    [Test]
    public async Task AuctionHouse_Currency_Conserved_WithDocumentedSinks()
    {
        SeedSurfaceAndReset();
        var fleetSize = 3;
        var (primary, fleet) = RigFleet(fleetSize);
        SeedWorkingAuctionMail(fleet);

        var result = AuctionHouseScenario.Run(primary, FixtureProvisioner(fleet),
            new AuctionHouseScenario.AuctionScenarioOptions(
                ItemTemplateId: RigItemTemplateId,
                SeedMoney: RigSeedMoney,
                BuyoutPrice: RigBuyout,
                Duration: AuctionDuration.AuctionDuration6Hours,
                FleetSize: fleetSize));

        await Assert.That(result.Passed).IsTrue().Because(result.Evidence());
        var currency = result.Criteria.First(c => c.Name == "currency-conservation");
        await Assert.That(currency.Passed).IsTrue();

        // fee = buyout×1%×(duration+1) = 1000×0.01×1 = 10; AH cut = 10% × 1000 = 100.
        // expected = 3×10000 − 3×10 − 3×100 = 29670.
        await Assert.That(currency.Detail).Contains("expected=29670");
    }

    [Test]
    public async Task AuctionHouse_TraceRecords_CompleteLifecycle()
    {
        SeedSurfaceAndReset();
        var fleetSize = 3;
        var (primary, fleet) = RigFleet(fleetSize);
        SeedWorkingAuctionMail(fleet);

        var result = AuctionHouseScenario.Run(primary, FixtureProvisioner(fleet),
            new AuctionHouseScenario.AuctionScenarioOptions(
                ItemTemplateId: RigItemTemplateId,
                SeedMoney: RigSeedMoney,
                BuyoutPrice: RigBuyout,
                Duration: AuctionDuration.AuctionDuration6Hours,
                FleetSize: fleetSize));

        await Assert.That(result.Passed).IsTrue().Because(result.Evidence());
        var trace = result.Criteria.First(c => c.Name == "trace-complete");
        await Assert.That(trace.Passed).IsTrue();
        await Assert.That(trace.Detail).Contains("completed=6"); // 3 posts + 3 buys
    }

    [Test]
    public async Task AuctionHouse_TraceRecords_ExposeRealServerTimestamps()
    {
        SeedSurfaceAndReset();
        var fleetSize = 3;
        var (primary, fleet) = RigFleet(fleetSize);
        SeedWorkingAuctionMail(fleet);

        var result = AuctionHouseScenario.Run(primary, FixtureProvisioner(fleet),
            new AuctionHouseScenario.AuctionScenarioOptions(
                ItemTemplateId: RigItemTemplateId,
                SeedMoney: RigSeedMoney,
                BuyoutPrice: RigBuyout,
                Duration: AuctionDuration.AuctionDuration6Hours,
                FleetSize: fleetSize));

        await Assert.That(result.Passed).IsTrue().Because(result.Evidence());
        // Evidence hygiene (t_6e2725b5): the result must expose the
        // per-action audit records in execution order — 2N completed
        // (posts + buys) + 1 dedupe-refusal — each carrying REAL server
        // timestamps, so a trace artifact written from these records is
        // attestable as "real output with timestamps" without any
        // worker-side transcription of the deterministic evidence block.
        var records = result.TraceRecords;
        await Assert.That(records.Count).IsEqualTo(2 * fleetSize + 1);
        await Assert.That(records.Count(r => r.Result == ActorLifecycleState.Completed)).IsEqualTo(2 * fleetSize);
        // Every record was requested server-side; every completed record
        // has a terminal time at/after its request time (real engine
        // transitions, not fabricated zeros).
        await Assert.That(records.All(r => r.RequestedAtUtc != default)).IsTrue();
        var completed = records.Where(r => r.Result == ActorLifecycleState.Completed).ToList();
        await Assert.That(completed.All(r =>
            r.CompletedAtUtc is { } done && done >= r.RequestedAtUtc)).IsTrue();
        // The trace preserves ring order: the first record is the primary's
        // post and the last completed record is the primary's closure buy.
        await Assert.That(records[0].Action).IsEqualTo(ActorActionType.AuctionPost);
        await Assert.That(completed[^1].Action).IsEqualTo(ActorActionType.AuctionBuy);
    }

    [Test]
    public async Task AuctionHouse_FleetSizeBelowTwo_Fails()
    {
        SeedSurfaceAndReset();
        var (primary, _) = RigFleet(1);

        var result = AuctionHouseScenario.Run(primary, FixtureProvisioner([primary]),
            new AuctionHouseScenario.AuctionScenarioOptions(
                ItemTemplateId: RigItemTemplateId,
                SeedMoney: RigSeedMoney,
                BuyoutPrice: RigBuyout,
                Duration: AuctionDuration.AuctionDuration6Hours,
                FleetSize: 1));

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailReason).Contains("fleet size");
    }

    [Test]
    public async Task AuctionHouse_ProvisionerFailure_FailsWithReason()
    {
        SeedSurfaceAndReset();
        var (primary, _) = RigFleet(1);

        var result = AuctionHouseScenario.Run(primary, (_, _) => null,
            new AuctionHouseScenario.AuctionScenarioOptions(
                ItemTemplateId: RigItemTemplateId,
                SeedMoney: RigSeedMoney,
                BuyoutPrice: RigBuyout,
                Duration: AuctionDuration.AuctionDuration6Hours,
                FleetSize: 3));

        await Assert.That(result.Passed).IsFalse();
        await Assert.That(result.FailReason).Contains("provisioning failed");
    }
}
