#nullable enable

using System.Numerics;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Goap;
using AAEmu.Game.Core.Managers.Bots.Goap.Actions;
using HeadlessSession = AAEmu.Game.Models.Game.Bots.HeadlessSession;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Units;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
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

        cache.StoreTemplate(state, labor: 100, gold: 500, goal, actions);

        await Assert.That(cache.TemplateCount).IsEqualTo(1);
        bool hit = cache.TryGetTemplate(state, labor: 100, gold: 500, goal, out var cachedNames);

        await Assert.That(hit).IsTrue();
        await Assert.That(cachedNames.Count).IsEqualTo(4);
        await Assert.That(cachedNames[0]).IsEqualTo("TravelToSeedMerchant");
        await Assert.That(cachedNames[1]).IsEqualTo("BuyTreeSaplings");
        await Assert.That(cachedNames[2]).IsEqualTo("HikeToSecretPlateau");
        await Assert.That(cachedNames[3]).IsEqualTo("PlantSecretGrove");
    }

    [Test]
    public async Task PlanTemplateCache_SameFlagsDifferentResources_DoesNotShareTemplate()
    {
        var cache = new PlanTemplateCache();
        var actions = new List<IGoapAction> { new AcquireScarecrowAction(), new SurveyAndPlacePlotAction() };

        ulong flags = BotWorldState.Empty.With(BotWorldState.HasScarecrowDesign).Flags;
        const string goal = "ClaimHomestead";

        // Flags (and therefore every flag-gated precondition) are identical; only resources differ.
        cache.StoreTemplate(flags, labor: 250, gold: 5000, goal, actions);

        await Assert.That(cache.TryGetTemplate(flags, labor: 10, gold: 0, goal, out _)).IsFalse();
        await Assert.That(cache.TryGetTemplate(flags, labor: 250, gold: 0, goal, out _)).IsFalse();
        await Assert.That(cache.TryGetTemplate(flags, labor: 10, gold: 5000, goal, out _)).IsFalse();
        await Assert.That(cache.TryGetTemplate(flags, labor: 250, gold: 5000, goal, out var same)).IsTrue();
        await Assert.That(same.Count).IsEqualTo(2);
        await Assert.That(cache.TemplateCount).IsEqualTo(1);
    }

    // ---------------------------------------------------------------- step-5 observations

    [Test]
    public async Task BagFull_FreshBot_False()
    {
        var provider = new BotWorldStateProvider();
        var bot = NewBot("empty-bag-bot");
        var context = new BotContext();

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.BagFull)).IsFalse();
    }

    [Test]
    public async Task BagFull_ZeroFreeSlots_True()
    {
        var provider = new BotWorldStateProvider();
        var bot = NewBot("full-bag-bot");
        bot.Character.Inventory.Bag.ContainerSize = 0; // no free slots -> full
        var context = new BotContext();

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.BagFull)).IsTrue();
    }

    [Test]
    public async Task BagFull_MatureGrove_DivergesFromFullness()
    {
        // A mature grove is a harvest opportunity, not a full bag: the grove
        // flag sets while BagFull stays clear on a non-full bag.
        var provider = new BotWorldStateProvider();
        var bot = NewBot("grove-bot");
        var context = new BotContext();
        context.Memory.AddGrove(1, new Vector3(0f, 0f, 0f), DateTime.UtcNow.AddMinutes(-30));
        await Assert.That(context.Memory.HasMatureGroves(context.GetUtcNow())).IsTrue();

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.SecretGrovePlanted)).IsTrue();
        await Assert.That(state.Has(BotWorldState.BagFull)).IsFalse();
    }

    [Test]
    public async Task Hostile_MonsterFactionNpc_True()
    {
        var provider = new BotWorldStateProvider();
        var bot = NewBot("hunter-bot");
        var npc = new Npc { Faction = new SystemFaction { Id = (FactionsEnum)115 } };
        npc.Hp = 500;
        bot.Character.CurrentTarget = npc;
        var context = new BotContext();

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.HasActiveTarget)).IsTrue();
        await Assert.That(state.Has(BotWorldState.TargetIsHostile)).IsTrue();
    }

    [Test]
    public async Task Hostile_PlayerTarget_NotHostile()
    {
        // Non-NPC targets short-circuit: a fellow player is never "hostile".
        var provider = new BotWorldStateProvider();
        var bot = NewBot("witness-bot");
        var friend = NewBot("friend-bot");
        bot.Character.CurrentTarget = friend.Character;
        var context = new BotContext();

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.HasActiveTarget)).IsTrue();
        await Assert.That(state.Has(BotWorldState.TargetIsHostile)).IsFalse();
    }

    [Test]
    public async Task Materials_EmptyBag_False()
    {
        var provider = new BotWorldStateProvider();
        var bot = NewBot("no-mats-bot");
        var context = new BotContext();

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.HasBuildingMaterials)).IsFalse();
    }

    [Test]
    public async Task Materials_PackCategoryItem_True()
    {
        // NewBot seeds the rig surface first; the trade template is additive.
        var provider = new BotWorldStateProvider();
        var bot = NewBot("pack-bot");
        GameplayActorTestRig.SeedTradeItemTemplate(777001u, price: 0, refund: 0, sellable: false);
        ItemManager.Instance.GetTemplate(777001u).CategoryId = (int)ItemCategory.Trade_Pack;
        var granted = bot.Character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Gm, 777001u, 1, 1);
        await Assert.That(granted).IsTrue();
        var context = new BotContext();

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.HasBuildingMaterials)).IsTrue();
    }

    [Test]
    public async Task Override_DesignIgnoredByDefault()
    {
        var provider = new BotWorldStateProvider();
        var bot = NewBot("no-gate-bot");
        var context = new BotContext();
        context.Memory.HasScarecrowDesignOverride = true; // stored, but gate closed
        await Assert.That(context.Memory.FixtureOverridesEnabled).IsFalse();

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.HasScarecrowDesign)).IsFalse();
    }

    [Test]
    public async Task Override_DesignHonoredWhenGateOpen()
    {
        var provider = new BotWorldStateProvider();
        var bot = NewBot("gate-bot");
        var context = new BotContext();
        context.Memory.EnableFixtureOverrides();
        context.Memory.HasScarecrowDesignOverride = true;

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.HasScarecrowDesign)).IsTrue();
    }

    [Test]
    public async Task LandPlot_StaleMemoryIdWithoutLiveHouse_False()
    {
        // A memory house id with no live house is stale — not a plot.
        var provider = new BotWorldStateProvider();
        var bot = NewBot("stale-plot-bot");
        var context = new BotContext();
        context.Memory.OwnedHouseId = 9999;

        var state = provider.Project(bot, context);

        await Assert.That(state.Has(BotWorldState.HasLandPlot)).IsFalse();
    }

}
