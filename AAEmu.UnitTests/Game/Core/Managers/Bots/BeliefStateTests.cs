using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Belief;

using Snapshot = AAEmu.Game.Core.Managers.Bots.BotPerceptionSnapshot.Snapshot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Belief layer (layer 4) fact derivation on synthetic frames: the interpreter
/// turns one snapshot plus its frame diff into fail-closed belief facts.
/// No live engine, no mocks of production logic — only fixture data.
/// Mirrors the PerceptionDeltaTests fixture style (Frame/Npc builders).
///
/// Targets the landed AAEmu.Game/Core/Managers/Bots/Belief/ surface:
/// BeliefFacts ulong consts, BeliefInputs(SelfObjId, CurrentTargetObjId,
/// SelfHpRatio, BagFreeSlots), CurrentPlace{RegionId, SubZoneId, SemanticPlace},
/// BeliefInterpreter.Interpret(snapshot, diff, place, inputs).
/// </summary>
public class BeliefStateTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
    private const uint SelfId = 1000;
    private const uint Boar3475 = 3475;

    [Test]
    public async Task OwnedLootableCorpse_SetsNearbyDistanceAndRef()
    {
        var before = Npc(7, 4f, hp: 100, lifecycle: PerceptionLifecycle.Live, owner: SelfId);
        var after = Npc(7, 4f, hp: 0, lifecycle: PerceptionLifecycle.Corpse,
            owner: SelfId, lootable: true, lootCount: 2);
        var (snap, diff) = Transition([before], [after]);

        await Assert.That(diff.BecameCorpse.Select(e => e.ObjId)).IsEquivalentTo([7u]);
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs());

        await Assert.That(belief.IsSet(BeliefFacts.OwnedCorpseNearby)).IsTrue();
        await Assert.That(belief.Numbers.OwnedCorpseDistance).IsEqualTo(4f);
        await Assert.That(belief.Refs.OwnedCorpseObjId).IsEqualTo(7u);
    }

    [Test]
    public async Task ForeignLootableCorpse_DoesNotSetOwned()
    {
        var before = Npc(7, 4f, hp: 100, lifecycle: PerceptionLifecycle.Live, owner: 777);
        var after = Npc(7, 4f, hp: 0, lifecycle: PerceptionLifecycle.Corpse,
            owner: 777, lootable: true, lootCount: 2);
        var (snap, diff) = Transition([before], [after]);

        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs());

        await Assert.That(belief.IsSet(BeliefFacts.OwnedCorpseNearby)).IsFalse();
    }

    [Test]
    public async Task OwnedUnlootableCorpse_DoesNotSetOwned()
    {
        var before = Npc(7, 4f, hp: 100, lifecycle: PerceptionLifecycle.Live, owner: SelfId);
        var after = Npc(7, 4f, hp: 0, lifecycle: PerceptionLifecycle.Dead,
            owner: SelfId, lootable: false, lootCount: 0);
        var (snap, diff) = Transition([before], [after]);

        await Assert.That(diff.BecameCorpse).IsEmpty();
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs());

        await Assert.That(belief.IsSet(BeliefFacts.OwnedCorpseNearby)).IsFalse();
    }

    [Test]
    public async Task NoTarget_TargetVisibleStaysUnknown_NeverFar()
    {
        // An entity is present but nothing is targeted: the fact is unknown,
        // never a fabricated "far".
        var (snap, diff) = Steady(Npc(7, 12f, hostile: true));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs(target: 0));

        await Assert.That(belief.IsKnown(BeliefFacts.TargetVisible)).IsFalse();
        await Assert.That(belief.IsSet(BeliefFacts.TargetVisible)).IsFalse();
        await Assert.That(float.IsNaN(belief.Numbers.TargetDistance)).IsTrue();
    }

    [Test]
    public async Task PresentTarget_SetsVisibleDistanceAndRef()
    {
        var (snap, diff) = Steady(Npc(7, 9f, hostile: true), Npc(8, 3f));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs(target: 7));

        await Assert.That(belief.IsSet(BeliefFacts.TargetVisible)).IsTrue();
        await Assert.That(belief.Numbers.TargetDistance).IsEqualTo(9f);
        await Assert.That(belief.Refs.TargetObjId).IsEqualTo(7u);
    }

    [Test]
    public async Task DisappearedTarget_ClearsVisible()
    {
        var (snap, diff) = Transition([Npc(7, 9f, hostile: true)], []);
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs(target: 7));

        await Assert.That(diff.Disappeared.Select(e => e.ObjId)).IsEquivalentTo([7u]);
        await Assert.That(belief.IsSet(BeliefFacts.TargetVisible)).IsFalse();
        await Assert.That(float.IsNaN(belief.Numbers.TargetDistance)).IsTrue();
    }

    [Test]
    public async Task MovedTarget_RewritesDistance()
    {
        var (snap, diff) = Transition([Npc(7, 10f, hostile: true)], [Npc(7, 6f, hostile: true)]);
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs(target: 7));

        await Assert.That(diff.Moved.Select(m => m.ObjId)).IsEquivalentTo([7u]);
        await Assert.That(belief.IsSet(BeliefFacts.TargetVisible)).IsTrue();
        await Assert.That(belief.Numbers.TargetDistance).IsEqualTo(6f);
    }
    [Test]
    public async Task UnknownHostileNpc_NeverCountsAsEnemy()
    {
        var (snap, diff) = Steady(Npc(7, 3f, hostile: null));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs());

        await Assert.That(belief.Numbers.EnemyCount).IsEqualTo(0);
        await Assert.That(belief.IsSet(BeliefFacts.EnemyNearby)).IsFalse();
        // Fail-closed: an unreadable hostility poisons the fact into UNKNOWN,
        // so "we could not tell" never reads as "nothing is here".
        await Assert.That(belief.IsKnown(BeliefFacts.EnemyNearby)).IsFalse();
    }

    [Test]
    public async Task CloseHostiles_CountAsEnemies()
    {
        var (snap, diff) = Steady(Npc(7, 5f, hostile: true), Npc(8, 6f, hostile: true), Npc(9, 4f));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs());

        await Assert.That(belief.Numbers.EnemyCount).IsEqualTo(2);
        await Assert.That(belief.IsSet(BeliefFacts.EnemyNearby)).IsTrue();
    }

    [Test]
    public async Task HpBand_OrdersThreatForTheSameEnemyCount()
    {
        var frame = new[] { Npc(7, 5f, hostile: true) };
        var healthy = BeliefInterpreter.Interpret(Steady(frame).Snapshot, Steady(frame).Diff, Place("field"), Inputs(hp: 0.51f));
        var wounded = BeliefInterpreter.Interpret(Steady(frame).Snapshot, Steady(frame).Diff, Place("field"), Inputs(hp: 0.49f));
        var critical = BeliefInterpreter.Interpret(Steady(frame).Snapshot, Steady(frame).Diff, Place("field"), Inputs(hp: 0.19f));

        await Assert.That((int)critical.Threat > (int)wounded.Threat).IsTrue();
        await Assert.That((int)wounded.Threat > (int)healthy.Threat).IsTrue();
    }

    [Test]
    public async Task BecameLooted_ClearsOwnedCorpse()
    {
        var corpse = Npc(7, 4f, hp: 0, lifecycle: PerceptionLifecycle.Corpse,
            owner: SelfId, lootable: true, lootCount: 2);
        var looted = Npc(7, 4f, hp: 0, lifecycle: PerceptionLifecycle.CorpseLooted,
            owner: SelfId, lootable: false, lootCount: 0);
        var (snap, diff) = Transition([corpse], [looted]);
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs());

        await Assert.That(diff.BecameLooted.Select(e => e.ObjId)).IsEquivalentTo([7u]);
        await Assert.That(belief.IsSet(BeliefFacts.OwnedCorpseNearby)).IsFalse();
        await Assert.That(float.IsNaN(belief.Numbers.OwnedCorpseDistance)).IsTrue();
        await Assert.That(belief.Refs.OwnedCorpseObjId).IsEqualTo(0u);
    }

    [Test]
    public async Task RemovedCorpse_ClearsOwnedCorpse()
    {
        var corpse = Npc(7, 4f, hp: 0, lifecycle: PerceptionLifecycle.Corpse,
            owner: SelfId, lootable: true, lootCount: 2);
        var (snap, diff) = Transition([corpse], []);
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs());

        await Assert.That(diff.Disappeared.Select(e => e.ObjId)).IsEquivalentTo([7u]);
        await Assert.That(diff.Disappeared[0].Lifecycle).IsEqualTo(PerceptionLifecycle.Removed);
        await Assert.That(belief.IsSet(BeliefFacts.OwnedCorpseNearby)).IsFalse();
        await Assert.That(float.IsNaN(belief.Numbers.OwnedCorpseDistance)).IsTrue();
        await Assert.That(belief.Refs.OwnedCorpseObjId).IsEqualTo(0u);
    }

    [Test]
    public async Task UnlimitedBag_InventoryFullStaysUnknown()
    {
        var (snap, diff) = Steady(Npc(7, 5f));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs(freeSlots: null));

        await Assert.That(belief.IsKnown(BeliefFacts.InventoryFull)).IsFalse();
        await Assert.That(belief.IsSet(BeliefFacts.InventoryFull)).IsFalse();
    }

    [Test]
    public async Task FullBag_SetsInventoryFull()
    {
        var (snap, diff) = Steady(Npc(7, 5f));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs(freeSlots: 0));

        await Assert.That(belief.IsKnown(BeliefFacts.InventoryFull)).IsTrue();
        await Assert.That(belief.IsSet(BeliefFacts.InventoryFull)).IsTrue();
    }

    [Test]
    public async Task BagWithRoom_InventoryFullKnownAbsent()
    {
        var (snap, diff) = Steady(Npc(7, 5f));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs(freeSlots: 8));

        await Assert.That(belief.IsKnown(BeliefFacts.InventoryFull)).IsTrue();
        await Assert.That(belief.IsSet(BeliefFacts.InventoryFull)).IsFalse();
    }

    [Test]
    public async Task Merchant_ResolvesPlaceKey()
    {
        var (snap, diff) = Steady(Npc(7, 6f, merchant: true));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("village"), Inputs());

        await Assert.That(belief.IsSet(BeliefFacts.MerchantNearby)).IsTrue();
        await Assert.That(belief.Refs.MerchantObjId).IsEqualTo(7u);
        await Assert.That(belief.Numbers.NearestMerchantDistance).IsEqualTo(6f);
        await Assert.That(belief.Refs.SemanticPlaceKey).IsEqualTo("village");
    }

    [Test]
    public async Task NearbyNonMerchant_DoesNotResolveMerchant()
    {
        // Distance alone never resolves the merchant fact — vendor role is required.
        var (snap, diff) = Steady(Npc(7, 2f, merchant: false));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("village"), Inputs());

        await Assert.That(belief.IsSet(BeliefFacts.MerchantNearby)).IsFalse();
    }

    [Test]
    public async Task FarmPlace_SetsAtFarm()
    {
        var (snap, diff) = Steady(Npc(7, 5f));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("farm"), Inputs());

        await Assert.That(belief.IsSet(BeliefFacts.AtFarm)).IsTrue();
    }

    [Test]
    public async Task NonFarmPlace_AtFarmAbsent()
    {
        var (snap, diff) = Steady(Npc(7, 5f));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("village"), Inputs());

        await Assert.That(belief.IsSet(BeliefFacts.AtFarm)).IsFalse();
    }

    [Test]
    public async Task FarmPlot_UnobservedStaysUnknown()
    {
        var (snap, diff) = Steady(Npc(7, 5f));
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("farm"), Inputs());

        await Assert.That(belief.IsKnown(BeliefFacts.FarmHasOpenPlot)).IsFalse();
        await Assert.That(belief.IsSet(BeliefFacts.FarmHasOpenPlot)).IsFalse();
    }

    [Test]
    public async Task EmptySnapshot_ThreatNoneAndNoEnemies()
    {
        var (snap, diff) = Steady();
        var belief = BeliefInterpreter.Interpret(snap, diff, Place("field"), Inputs());

        await Assert.That(belief.Threat).IsEqualTo(ThreatLevel.None);
        await Assert.That(belief.Numbers.EnemyCount).IsEqualTo(0);
    }

    // ------------------------------------------------------------- fixture

    private static BeliefInputs Inputs(uint target = 0, float hp = 1f, int? freeSlots = 8) => new(
        SelfObjId: SelfId, CurrentTargetObjId: target, SelfHpRatio: hp, BagFreeSlots: freeSlots);

    private static CurrentPlace Place(string? semantic) => new() { RegionId = 1, SubZoneId = 2, SemanticPlace = semantic };

    private static (Snapshot Snapshot, PerceptionFrameDiff Diff) Steady(params PerceivedEntity[] entities)
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, entities));
        var snap = Frame(T0.AddSeconds(1), entities);
        return (snap, delta.Advance(snap));
    }

    private static (Snapshot Snapshot, PerceptionFrameDiff Diff) Transition(
        PerceivedEntity[] before, PerceivedEntity[] after)
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, before));
        var snap = Frame(T0.AddSeconds(1), after);
        return (snap, delta.Advance(snap));
    }

    private static Snapshot Frame(DateTime at, params PerceivedEntity[] entities) => new()
    {
        ObservedAt = at,
        SelfPosition = Vector3.Zero,
        Entities = [.. entities.Select(e => e with { FirstSeen = e.FirstSeen == default ? at : e.FirstSeen, LastSeen = at })],
        Characters = [],
        Npcs = [.. entities.Where(e => e.Kind == RadarEntityKind.Npc)],
        Doodads = [.. entities.Where(e => e.Kind == RadarEntityKind.Doodad)],
        Corpses = [.. entities.Where(e => e.Corpse != null)]
    };

    private static PerceivedEntity Npc(
        uint objId,
        float distance,
        int? hp = 100,
        PerceptionLifecycle lifecycle = PerceptionLifecycle.Live,
        bool? hostile = false,
        bool? merchant = null,
        bool? questGiver = null,
        uint? owner = null,
        bool? lootable = null,
        int? lootCount = null) => new()
    {
        ObjId = objId,
        Kind = RadarEntityKind.Npc,
        TemplateId = Boar3475,
        Name = "belief-boar",
        Position = new Vector3(distance, 0, 0),
        Distance = distance,
        Lifecycle = lifecycle,
        IsTargetable = hp > 0,
        IsInteractable = false,
        FirstSeen = default,
        LastSeen = default,
        Npc = new PerceivedNpc(null, hp, 100, hp > 0, hostile, merchant, questGiver, owner, null),
        Corpse = lifecycle is PerceptionLifecycle.Corpse or PerceptionLifecycle.CorpseLooted || hp <= 0
            ? new PerceivedCorpse(Boar3475, owner, lootable, lootCount, lootCount == 0)
            : null
    };
}
