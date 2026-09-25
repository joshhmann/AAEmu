using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;

using Snapshot = AAEmu.Game.Core.Managers.Bots.BotPerceptionSnapshot.Snapshot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Perception stack layer 3 (TEMPORAL DIFF) on synthetic frames: every
/// transition the stack reports must be a diff of two snapshots — never an
/// engine event (the engine has no despawn event at all, and spawns only for
/// scripted spawners).
///
/// What these tests pin:
///  - the ring is bounded (16..32 frames) and Current/Previous/History track it;
///  - Appeared/Disappeared are objId-set differences, and a disappearance is
///    reported with the LAST observed row re-stamped Removed;
///  - Moved needs real displacement (sub-epsilon jitter is not motion);
///  - HealthChanged/BecameDead/BecameCorpse/BecameLootable/BecameLooted each
///    fire exactly on their own transition and not before/after;
///  - the first frame is Appeared-only, and diffing is deterministic.
/// </summary>
[NotInParallel]
public class PerceptionDeltaTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
    private const uint Boar3475 = 3475;

    [Test]
    public async Task Capacity_ClampsToBoundedRing()
    {
        await Assert.That(new PerceptionDelta(0).HistoryCapacity).IsEqualTo(PerceptionDelta.MinHistoryCapacity);
        await Assert.That(new PerceptionDelta(1000).HistoryCapacity).IsEqualTo(PerceptionDelta.MaxHistoryCapacity);
        await Assert.That(new PerceptionDelta().HistoryCapacity).IsEqualTo(PerceptionDelta.DefaultHistoryCapacity);
    }

    [Test]
    public async Task Ring_TracksCurrentPreviousHistory_AndDropsOldest()
    {
        var delta = new PerceptionDelta(PerceptionDelta.MinHistoryCapacity);
        for (var i = 0; i < PerceptionDelta.MinHistoryCapacity + 5; i++)
            delta.Advance(Frame(T0.AddSeconds(i), Npc(1, i)));

        await Assert.That(delta.FrameCount).IsEqualTo(PerceptionDelta.MinHistoryCapacity);
        await Assert.That(delta.HistoryCount).IsEqualTo(PerceptionDelta.MinHistoryCapacity - 2);
        await Assert.That(delta.Current!.ObservedAt).IsEqualTo(T0.AddSeconds(PerceptionDelta.MinHistoryCapacity + 4));
        await Assert.That(delta.Previous!.ObservedAt).IsEqualTo(T0.AddSeconds(PerceptionDelta.MinHistoryCapacity + 3));
        await Assert.That(delta.History[0].ObservedAt).IsEqualTo(T0.AddSeconds(5));
        // The diff always compares the newest pair, not the oldest retained one.
        await Assert.That(delta.Diff!.ObservedAtUtc).IsEqualTo(delta.Current!.ObservedAt);
    }

    [Test]
    public async Task FirstFrame_IsAppearedOnly()
    {
        var delta = new PerceptionDelta();
        var diff = delta.Advance(Frame(T0, Npc(1, 0), Npc(2, 1)));

        await Assert.That(diff.IsFirstFrame).IsTrue();
        await Assert.That(diff.PreviousObservedAtUtc).IsNull();
        await Assert.That(diff.Appeared.Select(e => e.ObjId)).IsEquivalentTo([1u, 2u]);
        await Assert.That(diff.Disappeared).IsEmpty();
        await Assert.That(diff.Moved).IsEmpty();
        await Assert.That(diff.HealthChanged).IsEmpty();
    }

    [Test]
    public async Task AppearedAndDisappeared_FollowObjIdSets()
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, Npc(1, 0)));
        var appeared = delta.Advance(Frame(T0.AddSeconds(1), Npc(1, 0), Npc(2, 1)));
        var disappeared = delta.Advance(Frame(T0.AddSeconds(2), Npc(2, 1)));

        await Assert.That(appeared.Appeared.Select(e => e.ObjId)).IsEquivalentTo([2u]);
        await Assert.That(appeared.Disappeared).IsEmpty();
        await Assert.That(disappeared.Disappeared.Select(e => e.ObjId)).IsEquivalentTo([1u]);
        // The removed row carries its last observed state, re-stamped.
        await Assert.That(disappeared.Disappeared[0].Lifecycle).IsEqualTo(PerceptionLifecycle.Removed);
        await Assert.That(disappeared.Disappeared[0].LastSeen).IsEqualTo(T0.AddSeconds(1));
    }

    [Test]
    public async Task RemovedEntity_ThatReturns_IsAppearedAgain()
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, Npc(1, 0)));
        delta.Advance(Frame(T0.AddSeconds(1)));
        var returns = delta.Advance(Frame(T0.AddSeconds(2), Npc(1, 0)));

        await Assert.That(returns.Appeared.Select(e => e.ObjId)).IsEquivalentTo([1u]);
        await Assert.That(returns.Disappeared).IsEmpty();
    }

    [Test]
    public async Task Moved_NeedsRealDisplacement_AndReportsIt()
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, Npc(1, 0f)));
        // Sub-epsilon jitter (the client movement model's own noise).
        var jitter = delta.Advance(Frame(T0.AddSeconds(1), Npc(1, PerceptionDelta.MoveEpsilonM / 4f)));
        var real = delta.Advance(Frame(T0.AddSeconds(2), Npc(1, 3f)));

        await Assert.That(jitter.Moved).IsEmpty();
        await Assert.That(real.Moved.Count).IsEqualTo(1);
        await Assert.That(real.Moved[0].ObjId).IsEqualTo(1u);
        await Assert.That(real.Moved[0].TraveledM).IsEqualTo(3f - (PerceptionDelta.MoveEpsilonM / 4f)).Within(1e-3f);
        await Assert.That(real.Moved[0].From.X).IsEqualTo(PerceptionDelta.MoveEpsilonM / 4f).Within(1e-4f);
        await Assert.That(real.Moved[0].To.X).IsEqualTo(3f).Within(1e-4f);
    }

    [Test]
    public async Task HealthChanged_AndBecameDead_FireOnTheDamageFrame()
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, Npc(1, 0, hp: 100)));
        var damaged = delta.Advance(Frame(T0.AddSeconds(1), Npc(1, 0, hp: 40)));
        var killed = delta.Advance(Frame(T0.AddSeconds(2), Npc(1, 0, hp: 0, lifecycle: PerceptionLifecycle.Dead)));

        await Assert.That(damaged.HealthChanged.Count).IsEqualTo(1);
        await Assert.That(damaged.HealthChanged[0].FromHp).IsEqualTo(100);
        await Assert.That(damaged.HealthChanged[0].ToHp).IsEqualTo(40);
        await Assert.That(damaged.BecameDead).IsEmpty();
        await Assert.That(killed.HealthChanged[0].FromHp).IsEqualTo(40);
        await Assert.That(killed.HealthChanged[0].ToHp).IsEqualTo(0);
        await Assert.That(killed.BecameDead.Select(e => e.ObjId)).IsEquivalentTo([1u]);
        await Assert.That(killed.BecameCorpse).IsEmpty();
    }

    [Test]
    public async Task BecameCorpseThenLootableThenLooted_FireOnceEach()
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, Npc(1, 0, hp: 100)));
        // Frame 2: dead with loot in reach → corpse + lootable together.
        var corpse = delta.Advance(Frame(T0.AddSeconds(1), Npc(1, 0, hp: 0, lifecycle: PerceptionLifecycle.Corpse, lootable: true, lootCount: 2)));
        // Frame 3: container emptied → looted.
        var looted = delta.Advance(Frame(T0.AddSeconds(2), Npc(1, 0, hp: 0, lifecycle: PerceptionLifecycle.CorpseLooted, lootable: false, lootCount: 0)));
        // Frame 4: no further state change → nothing re-fires.
        var steady = delta.Advance(Frame(T0.AddSeconds(3), Npc(1, 0, hp: 0, lifecycle: PerceptionLifecycle.CorpseLooted, lootable: false, lootCount: 0)));

        await Assert.That(corpse.BecameCorpse.Select(e => e.ObjId)).IsEquivalentTo([1u]);
        await Assert.That(corpse.BecameLootable.Select(e => e.ObjId)).IsEquivalentTo([1u]);
        await Assert.That(corpse.BecameLooted).IsEmpty();
        await Assert.That(looted.BecameLooted.Select(e => e.ObjId)).IsEquivalentTo([1u]);
        await Assert.That(looted.BecameCorpse).IsEmpty();
        await Assert.That(looted.BecameLootable).IsEmpty();
        await Assert.That(steady.IsEmpty).IsTrue();
    }

    [Test]
    public async Task DeadEntityEnteringTheFrame_IsNeverABecameAnything()
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, Npc(1, 0, hp: 100)));
        // A corpse the observer walks up to (first observation of that objId):
        // it appeared, but it did not BECOME dead/corpse within perception.
        var first = delta.Advance(Frame(T0.AddSeconds(1),
            Npc(1, 0, hp: 100),
            Npc(2, 1, hp: 0, lifecycle: PerceptionLifecycle.Corpse, lootable: true, lootCount: 1)));

        await Assert.That(first.Appeared.Select(e => e.ObjId)).IsEquivalentTo([2u]);
        await Assert.That(first.BecameDead).IsEmpty();
        await Assert.That(first.BecameCorpse).IsEmpty();
        await Assert.That(first.BecameLootable).IsEmpty();
    }

    [Test]
    public async Task DoodadWithoutHp_NeverReportsHealthOrDeath()
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, Doodad(9, 0)));
        var moved = delta.Advance(Frame(T0.AddSeconds(1), Doodad(9, 5)));

        await Assert.That(moved.HealthChanged).IsEmpty();
        await Assert.That(moved.BecameDead).IsEmpty();
        await Assert.That(moved.Moved.Count).IsEqualTo(1);
    }

    [Test]
    public async Task Derive_IsDeterministic()
    {
        var before = Frame(T0, Npc(1, 0, hp: 100), Npc(2, 1));
        var after = Frame(T0.AddSeconds(1), Npc(1, 4, hp: 10), Npc(3, 2));

        var first = PerceptionDelta.Derive(before, after);
        var second = PerceptionDelta.Derive(before, after);

        await Assert.That(first.Appeared.Select(e => e.ObjId).ToList())
            .IsEquivalentTo(second.Appeared.Select(e => e.ObjId).ToList());
        await Assert.That(first.Moved.Select(m => m.ObjId).ToList())
            .IsEquivalentTo(second.Moved.Select(m => m.ObjId).ToList());
        await Assert.That(first.HealthChanged.Select(h => h.ToHp).ToList())
            .IsEquivalentTo(second.HealthChanged.Select(h => h.ToHp).ToList());
    }

    [Test]
    public async Task Reset_DropsEveryFrame()
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, Npc(1, 0)));
        delta.Reset();

        await Assert.That(delta.Current).IsNull();
        await Assert.That(delta.Previous).IsNull();
        await Assert.That(delta.Diff).IsNull();
        await Assert.That(delta.FrameCount).IsEqualTo(0);
    }

    [Test]
    public async Task Delta_RunsTheWholeChain_OnTheRealActor()
    {
        // End-to-end on the real headless actor: perceive → capture → advance,
        // and the derived transitions follow the engine's own state changes.
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AAEmu.Game.Models.AppConfiguration.Instance.World ??= new AAEmu.Game.Models.Game.WorldConfig();
        GameplayActorTestRig.Seed();
        QuestBehavior.ClearCorpseMemory();
        QuestBehavior.ClearLootMemory();

        var (actor, session) = GameplayActorTestRig.CreateActor("delta-chain");
        var character = actor.Character;
        var actorRegion = session.World.GetRegionByPos(character.Transform.World.Position)!;
        actorRegion.AddObject(character);
        character.Region = actorRegion;
        var npcObjId = GameplayActorTestRig.SpawnNpc(session, 3475);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = new AAEmu.Game.Models.Game.NPChar.NpcTemplate { Id = 3475, Name = "delta-boar", Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.Transform.Local.SetPosition(new Vector3(2, 0, 0));
        var region = session.World.GetRegionByPos(new Vector3(2, 0, 0))!;
        region.AddObject(npc);
        npc.Region = region;
        GameplayActorTestRig.SeedItemTemplate(91_411);

        var delta = new PerceptionDelta();
        var spawn = delta.Advance(Capture(actor, delta));
        await Assert.That(spawn.IsFirstFrame).IsTrue();
        await Assert.That(spawn.Appeared.Select(e => e.ObjId)).Contains(npcObjId);

        var live = delta.Advance(Capture(actor, delta));
        await Assert.That(live.Appeared).IsEmpty();

        GameplayActorTestRig.SeedLootContainer(npc, (91_411, 1));
        npc.Hp = 0;
        var death = delta.Advance(Capture(actor, delta));
        await Assert.That(death.BecameDead.Select(e => e.ObjId)).Contains(npcObjId);
        await Assert.That(death.BecameCorpse.Select(e => e.ObjId)).Contains(npcObjId);
        await Assert.That(death.BecameLootable.Select(e => e.ObjId)).Contains(npcObjId);
        await Assert.That(delta.Current!.Corpses.Select(e => e.ObjId)).Contains(npcObjId);

        GameplayActorTestRig.SeedLootContainer(npc);
        var looted = delta.Advance(Capture(actor, delta));
        await Assert.That(looted.BecameLooted.Select(e => e.ObjId)).Contains(npcObjId);

        // Gone from the world → the last observed state returns as Removed.
        session.World.RemoveObject(npc);
        var gone = delta.Advance(Capture(actor, delta));
        await Assert.That(gone.Disappeared.Select(e => e.ObjId)).Contains(npcObjId);
        await Assert.That(gone.Disappeared.Single(e => e.ObjId == npcObjId).Lifecycle)
            .IsEqualTo(PerceptionLifecycle.Removed);
    }

    // ------------------------------------------------------------- fixture

    private static Snapshot Capture(GameplayActor actor, PerceptionDelta delta)
        => BotPerceptionSnapshot.Capture(
            BotRadarProjection.Project(actor.Observe(), actor.Character), actor.Character, delta.Current);

    private static Snapshot Frame(DateTime at, params PerceivedEntity[] entities) => new()
    {
        ObservedAt = at,
        SelfPosition = Vector3.Zero,
        // The real stack stamps every row with the frame time.
        Entities = [.. entities.Select(e => e with { FirstSeen = e.FirstSeen == default ? at : e.FirstSeen, LastSeen = at })],
        Characters = [],
        Npcs = [.. entities.Where(e => e.Kind == RadarEntityKind.Npc)],
        Doodads = [.. entities.Where(e => e.Kind == RadarEntityKind.Doodad)],
        Corpses = [.. entities.Where(e => e.Corpse != null)]
    };

    private static PerceivedEntity Npc(
        uint objId,
        float x,
        int? hp = 100,
        PerceptionLifecycle lifecycle = PerceptionLifecycle.Live,
        bool? lootable = null,
        int? lootCount = null) => new()
    {
        ObjId = objId,
        Kind = RadarEntityKind.Npc,
        TemplateId = Boar3475,
        Name = "delta-boar",
        Position = new Vector3(x, 0, 0),
        Distance = x,
        Lifecycle = lifecycle,
        IsTargetable = hp > 0,
        IsInteractable = true,
        FirstSeen = default,
        LastSeen = default,
        Npc = new PerceivedNpc(null, hp, 100, hp > 0, true, null, null, null, null),
        Corpse = lifecycle is PerceptionLifecycle.Corpse or PerceptionLifecycle.CorpseLooted || hp <= 0
            ? new PerceivedCorpse(Boar3475, null, lootable, lootCount, lootCount == 0)
            : null
    };

    private static PerceivedEntity Doodad(uint objId, float x) => new()
    {
        ObjId = objId,
        Kind = RadarEntityKind.Doodad,
        TemplateId = 90_001,
        Name = "delta-doodad",
        Position = new Vector3(x, 0, 0),
        Distance = x,
        Lifecycle = PerceptionLifecycle.Live,
        IsTargetable = false,
        IsInteractable = true,
        FirstSeen = default,
        LastSeen = default,
        Doodad = new PerceivedDoodad(null, null, null, null, null, true, null, null)
    };
}
