using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Items.Loots;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Models.Game.Quests.Acts;

/// <summary>
/// Regression coverage for aggro-ranked objectives receiving the victim from
/// the engine's Unit.DoDie kill event. Monster-hunt credit remains a separate
/// QuestManager event; this suite specifically exercises OnKill attribution.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class QuestActObjAggroTests
{
    private const uint QuestId = 90_001;
    private const uint ComponentId = 90_002;
    private const uint ActId = 90_003;
    private const uint NpcTemplateId = 90_004;
    private const uint WrongNpcTemplateId = 90_005;

    private static (Character Killer, Npc Victim, Npc WrongNpc, Quest Quest, QuestAct QuestAct) Setup()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("aggro-objective");
        var killer = actor.Character;
        var victim = new Npc
        {
            ObjId = 90_101,
            Id = 90_101,
            TemplateId = NpcTemplateId,
            Hp = 1,
            MaxHp = 1,
            Template = new NpcTemplate { Id = NpcTemplateId, Scale = 1f }
        };
        var wrongNpc = new Npc
        {
            ObjId = 90_102,
            Id = 90_102,
            TemplateId = WrongNpcTemplateId,
            Hp = 1,
            MaxHp = 1,
            Template = new NpcTemplate { Id = WrongNpcTemplateId, Scale = 1f }
        };
        session.World.AddObject(victim);
        session.World.AddObject(wrongNpc);
        var parentWorldField = typeof(AAEmu.Game.Models.Game.World.GameObject)
            .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        parentWorldField.SetValue(victim, session.World);
        parentWorldField.SetValue(wrongNpc, session.World);
        var lootField = typeof(ItemManager).GetField("_lootPackDroppingNpc",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        if (lootField.GetValue(ItemManager.Instance) is null)
            lootField.SetValue(ItemManager.Instance, new Dictionary<uint, List<LootPackDroppingNpc>>());

        var questTemplate = new QuestTemplate { Id = QuestId };
        var quest = new Quest(
            questTemplate,
            killer,
            Mock.Of<IQuestManager>().Object,
            Mock.Of<ITaskManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IExpressTextManager>().Object,
            Mock.Of<IWorldManager>().Object)
        {
            QuestAcceptorType = QuestAcceptorType.Npc,
            AcceptorId = NpcTemplateId
        };
        var componentTemplate = new QuestComponentTemplate(questTemplate)
        {
            Id = ComponentId,
            KindId = QuestComponentKind.Progress
        };
        var questStep = new QuestStep(QuestComponentKind.Progress, quest);
        var questComponent = new QuestComponent(questStep, componentTemplate);
        var actTemplate = new QuestActObjAggro(componentTemplate)
        {
            ActId = ActId,
            DetailId = ActId,
            ThisComponentObjectiveIndex = 0,
            Rank1 = 100,
            Rank1Ratio = 100
        };
        var questAct = new QuestAct(questComponent, actTemplate);
        questComponent.Acts.Add(questAct);
        actTemplate.InitializeAction(quest, questAct);
        return (killer, victim, wrongNpc, quest, questAct);
    }

    [Test]
    public async Task NpcDoDie_EmitsVictimTarget_AndCreditsAggroObjective()
    {
        var (killer, victim, _, quest, questAct) = Setup();
        OnKillArgs observed = null;
        killer.Events.OnKill += (_, args) => observed = args;

        victim.DoDie(killer, KillReason.Damage);

        await Assert.That(observed).IsNotNull();
        await Assert.That(observed.Target).IsSameReferenceAs(victim);
        await Assert.That(observed.Killer).IsSameReferenceAs(killer);
        await Assert.That(observed.Victim).IsSameReferenceAs(victim);
        await Assert.That(questAct.GetObjective(quest)).IsEqualTo(1);
    }

    [Test]
    public async Task OnKill_WithAttackerOrWrongNpcTarget_DoesNotCreditAggroObjective()
    {
        var (killer, _, wrongNpc, quest, questAct) = Setup();
        killer.Events.OnKill(killer, new OnKillArgs { Target = killer, Killer = killer });
        killer.Events.OnKill(killer, new OnKillArgs { Target = wrongNpc, Killer = killer, Victim = wrongNpc });

        await Assert.That(questAct.GetObjective(quest)).IsEqualTo(0);
    }
}
