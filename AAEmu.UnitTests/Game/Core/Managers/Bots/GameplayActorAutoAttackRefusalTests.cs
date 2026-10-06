using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// A refused auto-attack must be distinguishable from a landed one.
///
/// <c>GameplayActor.AutoAttack</c> discarded the <see cref="SkillResult"/> from
/// <c>skill.Use(...)</c> and reported <c>Complete(...)</c> unconditionally, so a
/// cast the engine REFUSED (e.g. <c>TooFarRange</c>) and a cast that actually
/// fired produced byte-identical detail:
/// <c>auto-attack started (4) on 44327</c>.
///
/// A lane run read that as <c>verb=AutoAttack state=Completed delta=0</c> and
/// concluded skill 4 never dealt damage — which was wrong: the boar's HP did
/// fall on later ticks. The refusal was invisible, so a zero delta could not be
/// told apart from "the engine never let the swing happen".
///
/// These tests drive the real <see cref="GameplayActor.AutoAttack"/> seam and
/// assert the observable contract: the detail states the engine's verdict.
/// </summary>
[NotInParallel]
public class GameplayActorAutoAttackRefusalTests
{
    /// <summary>Ranged NPC template id used by the rig's default spawn.</summary>
    private const uint RangedNpcTemplateId = 1000;

    private static readonly Vector3 Here = new(100f, 100f, 10f);

    /// <summary>
    /// The engine refuses the swing — the request still completes (the
    /// auto-attack loop starts either way; this is observability, not control
    /// flow) but the detail must name the refusal so a consumer can tell it
    /// apart from a landed swing.
    /// </summary>
    [Test]
    public async Task AutoAttack_TargetBeyondWeaponRange_DetailNamesTheEngineRefusal()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("autoattack-refused");
        var targetObjId = SpawnTarget(actor, session, Here + new Vector3(400f, 0f, 0f));

        var request = actor.AutoAttack(targetObjId);

        // Control flow is UNCHANGED by this fix: the request resolves exactly
        // as it did before — the refusal is reported, not converted into a
        // rejection.
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);

        // The observable contract: a refusal is visible in the detail.
        var detail = request.Detail ?? string.Empty;
        await Assert.That(detail).Contains("refused");
        await Assert.That(detail).Contains(nameof(SkillResult.TooFarRange));
    }

    /// <summary>
    /// A swing the engine ACCEPTS must NOT claim a refusal — otherwise the
    /// token is noise and the two cases are merely differently-worded noise.
    /// The landed detail still names the skill and target it dispatched.
    /// </summary>
    [Test]
    public async Task AutoAttack_TargetInRange_DetailReportsNoRefusal()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("autoattack-landed");
        var targetObjId = SpawnTarget(actor, session, Here + new Vector3(2f, 0f, 0f));

        var request = actor.AutoAttack(targetObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        var detail = request.Detail ?? string.Empty;
        await Assert.That(detail).DoesNotContain("refused");
        await Assert.That(detail).Contains("auto-attack started");
        await Assert.That(detail).Contains(targetObjId.ToString());
    }

    // ------------------------------------------------------------ fixture

    private static uint SpawnTarget(GameplayActor actor, HeadlessSession session, Vector3 npcPos)
    {
        GameplayActorTestRig.SetPosition(actor, Here);
        var region = session.World.GetRegionByPos(Here)
            ?? throw new InvalidOperationException("rig world has no region at the actor position");
        region.AddObject(actor.Character);
        actor.Character.Region = region;

        var npcObjId = GameplayActorTestRig.SpawnNpc(session, RangedNpcTemplateId);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = new NpcTemplate { Id = RangedNpcTemplateId, Scale = 1f };
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        GameplayActorTestRig.SetNpcPosition(session, npcObjId, npcPos);
        region.AddObject(npc);
        npc.Region = region;
        return npcObjId;
    }
}