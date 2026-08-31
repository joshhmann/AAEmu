using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Skills.Effects.Enums;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;
/// <summary>
/// PB-002 MateLevel narrow rig proof: GameplayActor.UseItem(29040, mateObjId)
/// → real skill path (Skill.Use → GetInitialTarget → Cast → ApplyEffects →
/// SpecialEffect.Apply → AddExp) → Mate.AddExp → headless-safe OnMateLevelUp
/// → QuestActObjMateLevel objective credit, all headless.
///
/// The canonical growth potion (item 29040 → skill 23085 → effect 32617 →
/// SpecialEffect 13221 AddExp 50,000) is BLOCKED by a canonical data gap:
/// skill 23085's unit_reqs row 43798 is kind 38 (MotherFactionOnly) with
/// value1 = 5, and no canonical system_factions row has mother_id 5 — the
/// engine refuses with skill_urk_mother_faction_only (SkillResult 115) before
/// any effect application. The positive proof therefore uses the REAL AddExp
/// effect (SpecialEffect 13221, 50,000 XP) verbatim on a fixture skill
/// (90_501) with the real 23085 target/range shape; the real-23085 refusal is
/// pinned by its own contract test below.
///
/// Threshold: canonical mate exp curve (levels 1-50 total_mate_exp from
/// compact.sqlite3); 41 × 50,000 = 2,050,000 ≥ 2,021,250 (level 50), 40 uses
/// = 2,000,000 &lt; 2,021,250 (level 49). After use 41 Mate.AddExp caps
/// Experience at 2,021,250 (overflow 28,750 subtracted), Level = 50.
///
/// Engine quirk documented by the cleanup test: QuestActObjMateLevel
/// CalculateObjective CONSUMES the summon item when the objective credits
/// (Cleanup=t). A step evaluation AFTER the event finds the bag empty and
/// resets the objective — so the event path is asserted immediately after the
/// final use, and the full-lifecycle advance test uses cleanup=false.
/// </summary>
[NotInParallel]
public class GameplayActorMateLevelRigTests
{
    // Fixture ids (90_xxx/91_xxx range, collision-free).
    private const uint MateGrowthItemId = 29_040;      // canonical potion template id (fixture-seeded use-skill)
    private const uint MateGrowthSkillId = 90_501;     // fixture skill carrying the REAL AddExp effect (13221)
    private const uint SummonMateItemId = 8_158;       // canonical summon item (fixture-seeded SummonMateTemplate)
    private const uint SummonMateNpcId = 5_430;        // canonical mate npc for 8158
    private const uint RealMatePotionSkillId = 23_085; // canonical skill 23085 (blocked by MotherFactionOnly=5)
    private const uint RealPotionItemId = 91_006;      // fixture item carrying the REAL skill 23085
    private const uint MateQuestNpcTemplateId = 91_521;

    private const byte MateLevelTarget = 50;
    private const int MateGrowthUses = 41;        // 41 × 50,000 = 2,050,000 ≥ 2,021,250 (level 50)
    private const int MateLevel50Exp = 2_021_250; // canonical total_mate_exp at level 50 (capped)

    private const uint MateQuestId = 91_501; // cleanup=false full-lifecycle quest
    private const uint MateQuestStartComponentId = 91_511;
    private const uint MateQuestProgressComponentId = 91_512;
    private const uint MateQuestReadyComponentId = 91_513;

    private const uint MateCleanupQuestId = 91_502; // cleanup=true event-path quest
    private const uint MateCleanupStartComponentId = 91_531;
    private const uint MateCleanupProgressComponentId = 91_532;
    private const uint MateCleanupReadyComponentId = 91_533;

    /// <summary>Canonical total_mate_exp by level (level 1 = index 0), compact.sqlite3 levels table.</summary>
    private static readonly int[] CanonicalMateExpByLevel =
    [
        0, 50, 250, 700, 1500, 2750, 4550, 7000, 10200, 14250,
        19250, 25300, 32500, 40950, 50750, 62000, 74800, 89250, 105450, 123500,
        143500, 165550, 189750, 216200, 245000, 276250, 310050, 346500, 385700, 427750,
        472750, 520800, 572000, 626450, 684250, 745500, 810300, 878750, 950950, 1027000,
        1107000, 1191050, 1279250, 1371700, 1468500, 1569750, 1675550, 1786000, 1901200, 2021250
    ];

    private sealed record ProofContext(GameplayActor Actor, HeadlessSession Session, Mate Mate, SummonMate SummonItem, Quest Quest);

    /// <summary>
    /// Shared proof setup: pilot singletons (REAL QuestManager +
    /// UnitRequirementsGameData from compact.sqlite3), fixture item/skill
    /// surface, canonical-mate-curve ExperienceManager swap, fixture quest
    /// (Start ConAcceptNpc → Progress QuestActObjMateLevel → Ready
    /// ConReportNpc), 41 growth potions + 1 summon item stocked through the
    /// REAL acquisition path, mate registered in world + MateManager and
    /// wired to the summon item, quest accepted through the real engine path.
    /// </summary>
    private static ProofContext SetupProof(string actorName, uint questId, uint startComponentId,
        uint progressComponentId, uint readyComponentId, bool cleanup)
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor(actorName);
        var character = session.Character;

        // Fixture surface: the growth potion (canonical template id, fixture
        // use-skill), the summon item (SummonMateTemplate so ItemManager.Create
        // produces a real SummonMate registered in _allItems), and the fixture
        // skill carrying the REAL AddExp special effect (13221, 50,000 XP).
        GameplayActorTestRig.SeedItemTemplate(MateGrowthItemId, MateGrowthSkillId, useSkillAsReagent: true);
        SeedSummonMateTemplate(SummonMateItemId, SummonMateNpcId);
        SeedMateGrowthSkill(MateGrowthSkillId);

        SeedMateLevelQuest(questId, startComponentId, progressComponentId, readyComponentId, cleanup);

        // Stock through the REAL acquisition path (AcquireDefaultItem →
        // ItemManager.Create → _allItems).
        GameplayActorTestRig.GrantItem(actor, MateGrowthItemId, MateGrowthUses);
        GameplayActorTestRig.GrantItem(actor, SummonMateItemId, 1);

        var summonItems = new List<Item>();
        character.Inventory.Bag.GetAllItemsByTemplate(SummonMateItemId, -1, out summonItems, out _);
        var summonItem = (SummonMate)summonItems.Single();

        // Register the mate in the world + MateManager and wire it to the
        // summon item (ItemId → UpdateMateItemData writes DetailLevel).
        var mateObjId = GameplayActorTestRig.SummonMate(session, actor, GameplayActorTestRig.MateObjId, tlId: 1);
        var mate = (Mate)session.World.GetUnit(mateObjId)!;
        mate.ItemId = summonItem.Id;
        mate.DbInfo = new MateDb { ItemId = summonItem.Id, Level = 1, Xp = 0, Name = "test-mate" };
        mate.Level = 1;
        mate.Experience = 0;
        mate.Transform.Local.SetPosition(character.Transform.World.Position);

        // Accept through the real engine path (Start ConAcceptNpc → Progress).
        var accept = actor.AcceptQuest(questId, QuestAcceptorType.Npc, MateQuestNpcTemplateId);
        if (accept.State != ActorLifecycleState.Completed)
            throw new InvalidOperationException($"accept failed: {accept.Detail}");

        var quest = character.Quests!.ActiveQuests[questId];
        return new ProofContext(actor, session, mate, summonItem, quest);
    }

    /// <summary>
    /// PASS: 41 real potion uses at the mate (GCD-paced ≥150 ms) drive the
    /// REAL AddExp effect → Mate.AddExp → OnMateLevelUp → objective credit.
    /// The mate reaches level 50 (Experience capped at 2,021,250), the
    /// summon item's DetailLevel follows, the objective credits to 1, and all
    /// 41 growth potions are consumed. cleanup=false keeps the summon item so
    /// the step machine advances Progress → Ready through the ordinary RunAct
    /// state check.
    /// </summary>
    [Test]
    public async Task UseItem_RealAddExpEffect_LevelsMateTo50_CreditsObjective_AdvancesToReady()
    {
        var ctx = SetupProof("pb-mate-level-proof", MateQuestId, MateQuestStartComponentId,
            MateQuestProgressComponentId, MateQuestReadyComponentId, cleanup: false);
        using var expSwap = InstallCanonicalMateExpCurve();

        var requests = new List<ActorRequest>();
        for (var i = 0; i < MateGrowthUses; i++)
        {
            requests.Add(ctx.Actor.UseItem(MateGrowthItemId, ctx.Mate.ObjId));
            Thread.Sleep(160);
        }

        await Assert.That(requests.All(r => r.State == ActorLifecycleState.Completed)).IsTrue();
        await Assert.That(ctx.Mate.Experience).IsEqualTo(MateLevel50Exp);
        await Assert.That(ctx.Mate.Level).IsEqualTo(MateLevelTarget);
        await Assert.That(ctx.SummonItem.DetailLevel).IsEqualTo(MateLevelTarget);
        await Assert.That(ctx.Quest.Objectives[0]).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(ctx.Actor, MateGrowthItemId)).IsEqualTo(0);

        var advance = ctx.Actor.AdvanceQuest(MateQuestId);
        await Assert.That(advance.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(ctx.Quest.Step).IsEqualTo(QuestComponentKind.Ready);
    }

    /// <summary>
    /// PASS (event path): the cleanup shape (mirrors canonical carrier 5464:
    /// item 8158, level 50, cleanup='t') credits the objective AND consumes
    /// the summon item the moment the final level-up fires OnMateLevelUp —
    /// asserted immediately, before any later RunAct (a step evaluation after
    /// the event would find the bag empty and reset the objective — the
    /// documented cleanup-consume quirk).
    /// </summary>
    [Test]
    public async Task UseItem_RealAddExpEffect_CleanupShape_CreditsObjectiveAndConsumesSummonItem()
    {
        var ctx = SetupProof("pb-mate-level-cleanup", MateCleanupQuestId, MateCleanupStartComponentId,
            MateCleanupProgressComponentId, MateCleanupReadyComponentId, cleanup: true);
        using var expSwap = InstallCanonicalMateExpCurve();

        for (var i = 0; i < MateGrowthUses; i++)
        {
            var request = ctx.Actor.UseItem(MateGrowthItemId, ctx.Mate.ObjId);
            if (request.State != ActorLifecycleState.Completed)
                throw new InvalidOperationException($"use {i} failed: {request.Detail}");
            Thread.Sleep(160);
        }

        await Assert.That(ctx.Quest.Objectives[0]).IsEqualTo(1);
        await Assert.That(GameplayActorTestRig.BagCount(ctx.Actor, SummonMateItemId)).IsEqualTo(0);
    }

    /// <summary>
    /// Control: an unresolvable target objId is Rejected before any engine
    /// execution — mate and objective stay untouched, no potion consumed.
    /// </summary>
    [Test]
    public async Task UseItem_UnregisteredTarget_Rejected_StateUnchanged()
    {
        var ctx = SetupProof("pb-mate-ctrl-target", MateQuestId, MateQuestStartComponentId,
            MateQuestProgressComponentId, MateQuestReadyComponentId, cleanup: false);
        using var expSwap = InstallCanonicalMateExpCurve();

        var request = ctx.Actor.UseItem(MateGrowthItemId, 0x7FFF_FFFF);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(ctx.Mate.Experience).IsEqualTo(0);
        await Assert.That(ctx.Mate.Level).IsEqualTo((byte)1);
        await Assert.That(ctx.Quest.Objectives[0]).IsEqualTo(0);
        await Assert.That(GameplayActorTestRig.BagCount(ctx.Actor, MateGrowthItemId)).IsEqualTo(MateGrowthUses);
    }

    /// <summary>
    /// Control: with no growth potion stocked, UseItem is Rejected at the
    /// inventory lookup — mate state unchanged.
    /// </summary>
    [Test]
    public async Task UseItem_NoGrowthItemStocked_Rejected_StateUnchanged()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor("pb-mate-ctrl-noitem");
        GameplayActorTestRig.SeedItemTemplate(MateGrowthItemId, MateGrowthSkillId, useSkillAsReagent: true);
        SeedSummonMateTemplate(SummonMateItemId, SummonMateNpcId);
        SeedMateGrowthSkill(MateGrowthSkillId);
        GameplayActorTestRig.GrantItem(actor, SummonMateItemId, 1);

        var summonItems = new List<Item>();
        actor.Character.Inventory.Bag.GetAllItemsByTemplate(SummonMateItemId, -1, out summonItems, out _);
        var mateObjId = GameplayActorTestRig.SummonMate(session, actor, GameplayActorTestRig.MateObjId, tlId: 1);
        var mate = (Mate)session.World.GetUnit(mateObjId)!;
        mate.ItemId = ((SummonMate)summonItems.Single()).Id;
        mate.DbInfo = new MateDb { ItemId = mate.ItemId, Level = 1, Xp = 0, Name = "test-mate" };
        mate.Level = 1;
        mate.Experience = 0;

        var request = actor.UseItem(MateGrowthItemId, mate.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(mate.Level).IsEqualTo((byte)1);
        await Assert.That(mate.Experience).IsEqualTo(0);
    }

    /// <summary>
    /// Contract test for the canonical gap: the REAL skill 23085 carries
    /// unit_reqs row 43798 (kind 38 MotherFactionOnly, value1 5) — no
    /// canonical system_factions row has mother_id 5, so the engine refuses
    /// with skill_urk_mother_faction_only (SkillResult 115) BEFORE any effect
    /// application. The potion is not consumed and the mate is untouched.
    /// </summary>
    [Test]
    public async Task UseItem_RealSkill23085_RefusedByMotherFactionRequirement()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (actor, session) = GameplayActorTestRig.CreateActor("pb-mate-23085");
        GameplayActorTestRig.SeedItemTemplate(RealPotionItemId, RealMatePotionSkillId, useSkillAsReagent: true);
        SeedRealMatePotionSkill();
        GameplayActorTestRig.GrantItem(actor, RealPotionItemId, 1);

        var mateObjId = GameplayActorTestRig.SummonMate(session, actor, GameplayActorTestRig.MateObjId, tlId: 1);
        var mate = (Mate)session.World.GetUnit(mateObjId)!;
        mate.Level = 1;
        mate.Experience = 0;

        var request = actor.UseItem(RealPotionItemId, mate.ObjId);

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Rejected);
        await Assert.That(request.Failure).IsEqualTo(ActorFailureReason.RejectedAction);
        await Assert.That(mate.Experience).IsEqualTo(0);
        await Assert.That(mate.Level).IsEqualTo((byte)1);
        await Assert.That(GameplayActorTestRig.BagCount(actor, RealPotionItemId)).IsEqualTo(1);
    }

    // ------------------------------------------------------------------ helpers

    private static void SeedMateLevelQuest(uint questId, uint startComponentId, uint progressComponentId,
        uint readyComponentId, bool cleanup)
    {
        GameplayActorTestRig.SeedQuestOffer(questId, startComponentId, MateQuestNpcTemplateId, level: 1);
        var manager = QuestManager.Instance;
        var questTemplates = (Dictionary<uint, QuestTemplate>)GameplayActorTestRig.GetField(manager, "_questTemplates");
        var questTemplate = questTemplates[questId];
        var componentTemplates = (Dictionary<uint, QuestComponentTemplate>)GameplayActorTestRig.GetField(manager, "_componentTemplates");

        var progress = new QuestComponentTemplate(questTemplate)
        {
            Id = progressComponentId,
            KindId = QuestComponentKind.Progress
        };
        componentTemplates[progressComponentId] = progress;
        questTemplate.Components[progressComponentId] = progress;
        var mateLevelAct = new QuestActObjMateLevel(progress)
        {
            DetailId = progressComponentId,
            ActId = progressComponentId,
            ItemId = SummonMateItemId,
            Level = MateLevelTarget,
            Cleanup = cleanup,
            ThisComponentObjectiveIndex = 0
        };
        progress.ActTemplates.Add(mateLevelAct);

        var ready = new QuestComponentTemplate(questTemplate)
        {
            Id = readyComponentId,
            KindId = QuestComponentKind.Ready
        };
        componentTemplates[readyComponentId] = ready;
        questTemplate.Components[readyComponentId] = ready;
        var reportAct = new QuestActConReportNpc(ready)
        {
            DetailId = readyComponentId,
            ActId = readyComponentId,
            NpcId = MateQuestNpcTemplateId
        };
        ready.ActTemplates.Add(reportAct);
    }

    private static void SeedSummonMateTemplate(uint templateId, uint npcId)
    {
        var templates = (Dictionary<uint, ItemTemplate>)GameplayActorTestRig.GetField(ItemManager.Instance, "_templates");
        if (!templates.TryGetValue(templateId, out var existing) || existing is not SummonMateTemplate)
            templates[templateId] = new SummonMateTemplate { Id = templateId, NpcId = npcId, MaxCount = 1 };
    }

    /// <summary>
    /// Fixture skill with the REAL AddExp effect (SpecialEffect 13221,
    /// 50,000 XP) and the real 23085 target/range shape (Others/Target,
    /// 25 m, instant, no cooldown, default GCD).
    /// </summary>
    private static void SeedMateGrowthSkill(uint skillId)
    {
        GameplayActorTestRig.SeedSkillTemplate(skillId);
        var skills = (Dictionary<uint, SkillTemplate>)GameplayActorTestRig.GetField(SkillManager.Instance, "_skills");
        var template = skills[skillId];
        template.TargetType = SkillTargetType.Others;
        template.TargetSelection = SkillTargetSelection.Target;
        template.MaxRange = 25;
        template.MinRange = 0;
        template.DefaultGcd = true;
        template.Effects.Clear();
        template.Effects.Add(new SkillEffect
        {
            EffectId = 13_221,
            Template = new SpecialEffect
            {
                Id = 13_221,
                SpecialEffectTypeId = SpecialType.AddExp,
                Value1 = 50_000
            },
            StartLevel = 1,
            EndLevel = 99,
            Friendly = true,
            NonFriendly = true,
            Chance = 10_000,
            ApplicationMethod = SkillEffectApplicationMethod.Target,
            ConsumeItemCount = 1
        });
    }

    /// <summary>
    /// The REAL skill 23085 template shape (target/range/controller) with the
    /// real effect chain (effect 32617 → SpecialEffect 13221 AddExp 50,000).
    /// The unit_reqs gate (kind 38 MotherFactionOnly=5) comes from the REAL
    /// loaded UnitRequirementsGameData and refuses before effects apply.
    /// </summary>
    private static void SeedRealMatePotionSkill()
    {
        var skills = (Dictionary<uint, SkillTemplate>)GameplayActorTestRig.GetField(SkillManager.Instance, "_skills");
        if (!skills.TryGetValue(RealMatePotionSkillId, out var template))
        {
            template = new SkillTemplate
            {
                Id = RealMatePotionSkillId,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = 0,
                MinRange = 0,
                MaxRange = 25,
                TargetType = SkillTargetType.Others,
                TargetSelection = SkillTargetSelection.Target,
                DefaultGcd = true,
                CancelOngoingBuffs = true,
                SkillControllerId = 703
            };
            skills[RealMatePotionSkillId] = template;
        }
        template.Effects.Clear();
        template.Effects.Add(new SkillEffect
        {
            EffectId = 32_617,
            Template = new SpecialEffect
            {
                Id = 13_221,
                SpecialEffectTypeId = SpecialType.AddExp,
                Value1 = 50_000
            },
            StartLevel = 1,
            EndLevel = 99,
            Friendly = true,
            NonFriendly = true,
            Chance = 10_000,
            ApplicationMethod = SkillEffectApplicationMethod.Target,
            ConsumeItemCount = 1
        });
    }

    /// <summary>Swaps in the canonical mate exp curve (levels 1-50).</summary>
    private static SingletonSwap InstallCanonicalMateExpCurve()
    {
        var experienceManager = new ExperienceManager();
        var expTemplates = new List<ExperienceLevelTemplate>();
        for (var level = 1; level <= 50; level++)
        {
            expTemplates.Add(new ExperienceLevelTemplate
            {
                Level = (byte)level,
                TotalExp = level * 1000,
                TotalMateExp = CanonicalMateExpByLevel[level - 1],
                SkillPoints = 1
            });
        }
        SetField(experienceManager, "_levelTemplatesByLevel", expTemplates);
        SetField(experienceManager, "_expByLevel", expTemplates.Select(t => t.TotalExp).ToList());
        SetField(experienceManager, "_mateExpByLevel", CanonicalMateExpByLevel.ToList());
        SetField(experienceManager, "<MaxPlayerLevel>k__BackingField", (byte)50);
        SetField(experienceManager, "<MaxMateLevel>k__BackingField", (byte)50);
        return SingletonSwap.Install(typeof(Singleton<ExperienceManager>), experienceManager);
    }

    private static void SetField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(target, value);
    }

    /// <summary>Capture-and-force singleton swap; dispose restores the previous instance.</summary>
    private sealed class SingletonSwap : IDisposable
    {
        private readonly Type _singletonBase;
        private readonly object? _previous;

        private SingletonSwap(Type singletonBase)
        {
            _singletonBase = singletonBase;
            _previous = GetSingletonInstance(singletonBase);
        }

        public static SingletonSwap Install(Type singletonBase, object replacement)
        {
            var swap = new SingletonSwap(singletonBase);
            SetSingleton(singletonBase, replacement);
            return swap;
        }

        public void Dispose() => SetSingleton(_singletonBase, _previous!);

        private static object? GetSingletonInstance(Type singletonBase)
            => singletonBase.GetField("s_instance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null);

        private static void SetSingleton(Type singletonBase, object? instance)
            => singletonBase.GetField("s_instance",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.SetValue(null, instance);
    }
}
