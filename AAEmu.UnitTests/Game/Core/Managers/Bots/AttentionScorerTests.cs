using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Belief;

using Snapshot = AAEmu.Game.Core.Managers.Bots.BotPerceptionSnapshot.Snapshot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Attention scorer ordering on synthetic frames: the ranker turns one
/// snapshot plus its frame diff into a deterministic objId ranking.
/// No live engine, no mocks of production logic — only fixture data.
/// Mirrors the PerceptionDeltaTests fixture style (Frame/Npc builders).
///
/// Targets the landed AAEmu.Game/Core/Managers/Bots/Belief/ surface:
/// AttentionScorer.Rank(snapshot, diff, topK) returning objIds in rank order.
/// Target band re-asserts hostility (IsTargetable AND Npc.Hostile), corpse band
/// needs Lootable without diff-clear, quest band needs QuestGiver.
/// </summary>
public class AttentionScorerTests
{
    private static readonly DateTime T0 = new(2026, 9, 24, 10, 0, 0, DateTimeKind.Utc);
    private const uint Boar3475 = 3475;

    [Test]
    public async Task Rank_OrdersTargetAboveCorpseAboveQuestAboveNeutral()
    {
        var target = Npc(11, 9f, hostile: true); // IsTargetable → top
        var corpse = Npc(7, 4f, hp: 0, lifecycle: PerceptionLifecycle.Corpse,
            lootable: true, lootCount: 2); // owned-corpse band
        var quest = Npc(9, 6f, hostile: false, questGiver: true) with { IsTargetable = false };
        var neutral = Npc(13, 5f);
        var (snap, diff) = Steady(target, corpse, quest, neutral);

        var ranked = AttentionScorer.Rank(snap, diff, 10);

        await Assert.That(ranked.ToList()).IsEquivalentTo([11u, 7u, 9u, 13u]);
        await Assert.That(ranked[0]).IsEqualTo(11u);
        await Assert.That(ranked[1]).IsEqualTo(7u);
        await Assert.That(ranked[2]).IsEqualTo(9u);
        await Assert.That(ranked[3]).IsEqualTo(13u);
    }
    [Test]
    public async Task Rank_BreaksEqualScoresByLowerObjId()
    {
        // Three identical neutral rows: same score, so objId ascending wins.
        var (snap, diff) = Steady(Npc(21, 5f), Npc(19, 5f), Npc(23, 5f));

        var ranked = AttentionScorer.Rank(snap, diff, 10);

        await Assert.That(ranked.Count).IsEqualTo(3);
        await Assert.That(ranked[0]).IsEqualTo(19u);
        await Assert.That(ranked[1]).IsEqualTo(21u);
        await Assert.That(ranked[2]).IsEqualTo(23u);
    }

    [Test]
    public async Task Rank_TruncatesToTopK_BestFirst()
    {
        var target = Npc(11, 9f, hostile: true);
        var corpse = Npc(7, 4f, hp: 0, lifecycle: PerceptionLifecycle.Corpse,
            lootable: true, lootCount: 2);
        var quest = Npc(9, 6f, hostile: false, questGiver: true) with { IsTargetable = false };
        var neutral = Npc(13, 5f);
        var (snap, diff) = Steady(target, corpse, quest, neutral);

        var ranked = AttentionScorer.Rank(snap, diff, 2);

        await Assert.That(ranked.Count).IsEqualTo(2);
        await Assert.That(ranked[0]).IsEqualTo(11u);
        await Assert.That(ranked[1]).IsEqualTo(7u);
    }

    [Test]
    public async Task Rank_TopKZero_ReturnsEmpty()
    {
        var (snap, diff) = Steady(Npc(11, 9f, hostile: true));

        var ranked = AttentionScorer.Rank(snap, diff, 0);

        await Assert.That(ranked).IsEmpty();
    }

    [Test]
    public async Task Rank_EmptySnapshot_ReturnsEmpty()
    {
        var (snap, diff) = Steady();

        var ranked = AttentionScorer.Rank(snap, diff, 10);

        await Assert.That(ranked).IsEmpty();
        await Assert.That(diff.IsFirstFrame).IsFalse();
    }

    [Test]
    public async Task Rank_IsDeterministic()
    {
        var (snap, diff) = Steady(
            Npc(11, 9f, hostile: true),
            Npc(7, 4f, hp: 0, lifecycle: PerceptionLifecycle.Corpse, lootable: true, lootCount: 2),
            Npc(13, 5f));

        var first = AttentionScorer.Rank(snap, diff, 10);
        var second = AttentionScorer.Rank(snap, diff, 10);

        await Assert.That(first.ToList()).IsEquivalentTo(second.ToList());
    }

    // ------------------------------------------------------------- fixture

    private static (Snapshot Snapshot, PerceptionFrameDiff Diff) Steady(params PerceivedEntity[] entities)
    {
        var delta = new PerceptionDelta();
        delta.Advance(Frame(T0, entities));
        var snap = Frame(T0.AddSeconds(1), entities);
        return (snap, delta.Advance(snap));
    }

    private static Snapshot Frame(DateTime at, params PerceivedEntity[] entities) => new()
    {
        ObservedAt = at,
        SelfPosition = Vector3.Zero,
        Entities = [.. entities.Select(e => e with { FirstSeen = e.FirstSeen == default ? at : e.FirstSeen, LastSeen = at })],
        Characters = [.. entities.Where(e => e.Kind == RadarEntityKind.Character)],
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
        Name = "attention-boar",
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
