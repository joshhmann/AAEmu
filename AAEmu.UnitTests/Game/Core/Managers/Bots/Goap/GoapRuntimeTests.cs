#nullable enable

using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Goap;
using AAEmu.Game.Core.Managers.Bots.Goap.Actions;
using HeadlessSession = AAEmu.Game.Models.Game.Bots.HeadlessSession;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.Goap;

public class GoapRuntimeTests
{
    private static PlayerBotRuntime NewBot(string name, int hp = 1000, int maxHp = 1000, short labor = 100, long money = 500)
    {
        GameplayActorTestRig.Seed();
        var session = HeadlessSession.Create((uint)name.GetHashCode() & 0xFFFF, name, 1, Race.Nuian);
        var ch = session.Character;
        ch.Hp = hp;
        ch.MaxHp = maxHp;
        ch.Mp = 500;
        ch.MaxMp = 500;
        ch.LaborPower = labor;
        ch.Money = money;
        return new PlayerBotRuntime(ch, "runtime-tests");
    }

    [Test]
    public async Task BotWorldStateProvider_ProjectsVitalsAndCurrencies()
    {
        var provider = new BotWorldStateProvider();
        var bot = NewBot("vitals-bot", hp: 50, maxHp: 1000, labor: 75, money: 250);
        var context = new BotContext();

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.LowHealth)).IsTrue();
        await Assert.That(state.Has(BotWorldState.Recovered)).IsFalse();
        await Assert.That(state.Labor).IsEqualTo((ushort)75);
        await Assert.That(state.Gold).IsEqualTo((uint)250);
    }

    [Test]
    public async Task BotWorldStateProvider_DetectsCombatAndStance()
    {
        var provider = new BotWorldStateProvider();
        var bot = NewBot("combat-bot");
        bot.Character.IsInBattle = true;
        bot.Character.Stance = UnitStance.Sit;
        var context = new BotContext();

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.InCombat)).IsTrue();
        await Assert.That(state.Has(BotWorldState.IsSitting)).IsTrue();
    }

    [Test]
    public async Task BotWorldStateProvider_DetectsSpatialProximity()
    {
        var provider = new BotWorldStateProvider();
        var bot = NewBot("spatial-bot");
        bot.Character.Transform.World.Position = new Vector3(100f, 100f, 10f);

        var context = new BotContext();
        context.Memory.KnownSeedMerchantPos = new Vector3(105f, 100f, 10f); // 5m away -> near
        context.Memory.TargetWildFarmPos = new Vector3(500f, 500f, 10f);    // 565m away -> far

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.NearSeedMerchant)).IsTrue();
        await Assert.That(state.Has(BotWorldState.AtWildFarm)).IsFalse();
    }

    [Test]
    public async Task GoalArbitrator_PrioritizesSurvivalWhenLowHealth()
    {
        var arbitrator = new GoalArbitrator();
        var bot = NewBot("survival-bot", hp: 200, maxHp: 1000);
        var context = new BotContext();
        var state = BotWorldState.Empty.With(BotWorldState.LowHealth);

        var goal = arbitrator.ArbitrateGoal(bot, state, context, currentPrimaryGoal: GoalArbitrator.GoalPlantWildFarm);

        await Assert.That(goal).IsNotNull();
        await Assert.That(goal!.Name).IsEqualTo("Recover");
    }

    [Test]
    public async Task GoalArbitrator_PrioritizesCombatThreatOverCommittedIntent()
    {
        var arbitrator = new GoalArbitrator();
        var bot = NewBot("threat-bot");
        var context = new BotContext();
        var state = BotWorldState.Empty
            .With(BotWorldState.InCombat)
            .With(BotWorldState.HasActiveTarget)
            .With(BotWorldState.TargetIsHostile);

        var goal = arbitrator.ArbitrateGoal(bot, state, context, currentPrimaryGoal: GoalArbitrator.GoalPlantWildFarm);

        await Assert.That(goal).IsNotNull();
        await Assert.That(goal!.Name).IsEqualTo("DefendSelf");
    }

    [Test]
    public async Task GoalArbitrator_PreservesCommittedPrimaryIntentWhenSafe()
    {
        var arbitrator = new GoalArbitrator();
        var bot = NewBot("intent-bot");
        var context = new BotContext();
        var state = BotWorldState.Empty.WithLabor(100).WithGold(500);

        var goal = arbitrator.ArbitrateGoal(bot, state, context, currentPrimaryGoal: GoalArbitrator.GoalPlantWildFarm);

        await Assert.That(goal).IsNotNull();
        await Assert.That(goal!.Name).IsEqualTo("PlantWildFarm");
    }

    [Test]
    public async Task PlanTemplateCache_StoresAndRetrievesActionSequences()
    {
        var cache = new PlanTemplateCache();
        var actions = new List<IGoapAction>
        {
            new TravelToSeedMerchantAction(),
            new BuySaplingsAction(),
            new HikeToWildFarmAction(),
            new PlantWildSaplingAction()
        };

        ulong state = BotWorldState.Empty.Flags;
        string goal = "PlantWildFarm";

        cache.StoreTemplate(state, goal, actions);

        await Assert.That(cache.TemplateCount).IsEqualTo(1);
        bool hit = cache.TryGetTemplate(state, goal, out var cachedNames);

        await Assert.That(hit).IsTrue();
        await Assert.That(cachedNames.Count).IsEqualTo(4);
        await Assert.That(cachedNames[0]).IsEqualTo("TravelToSeedMerchant");
        await Assert.That(cachedNames[1]).IsEqualTo("BuyTreeSaplings");
        await Assert.That(cachedNames[2]).IsEqualTo("HikeToSecretPlateau");
        await Assert.That(cachedNames[3]).IsEqualTo("PlantSecretGrove");
    }
}
