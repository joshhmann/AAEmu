using AAEmu.Commons.Utils;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Housing;
using AAEmu.UnitTests.Game.Models.Game.World.Zones;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Conflict-join module rig (Q8 war-horn model): CanActivate gate matrix.
/// Fail-pre discipline: each deny case FAILS if the module allows, each
/// allow case FAILS if it denies or names the activity unstably.
/// </summary>
[ParallelLimiter<SequentialParallelLimit>]
[NotInParallel]
public class ConflictJoinActivityModuleTests
{
    private const uint TestZoneKey = 0x5A5A_0003;
    private const uint TestZoneGroupId = 0x5A7B;

    private sealed class ZoneScope : IDisposable
    {
        private readonly IDisposable _swap;
        private readonly Dictionary<uint, Zone> _zones;

        public ZoneScope(TestableZoneConflict conflict)
        {
            var zoneManager = new ZoneManager(Mock.Of<AAEmu.Game.Core.Managers.World.IWorldManager>().Object);
            SetField(zoneManager, "_zoneIdToKey", new Dictionary<uint, uint>());
            _zones = [];
            SetField(zoneManager, "_zones", _zones);
            SetField(zoneManager, "_groups", new Dictionary<uint, ZoneGroup>());
            SetField(zoneManager, "_conflicts", new Dictionary<ushort, ZoneConflict> { [(ushort)TestZoneGroupId] = conflict });
            SetField(zoneManager, "_groupBannedTags", new Dictionary<uint, ZoneGroupBannedTag>());
            SetField(zoneManager, "_climateElem", new Dictionary<uint, ZoneClimateElem>());
            _swap = SingletonSwap.Install(typeof(Singleton<ZoneManager>), zoneManager);
        }

        public void RegisterZone(uint zoneKey)
        {
            _zones[zoneKey] = new Zone
            {
                Id = zoneKey,
                ZoneKey = zoneKey,
                GroupId = TestZoneGroupId,
                FactionId = FactionsEnum.Neutral
            };
        }

        public void Dispose() => _swap.Dispose();
    }

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
            SetSingletonInstance(singletonBase, replacement);
            return swap;
        }

        public void Dispose() => SetSingletonInstance(_singletonBase, _previous!);
    }

    private static object? GetSingletonInstance(Type singletonBase)
        => singletonBase.GetField("s_instance",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.GetValue(null);

    private static void SetSingletonInstance(Type singletonBase, object instance)
        => singletonBase.GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, instance);

    private static void SetField(object target, string fieldName, object value)
    {
        target.GetType()
            .GetField(fieldName, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(target, value);
    }

    private static void SetZoneId(GameplayActor actor, uint zoneKey)
        => typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_zoneId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(actor.Character.Transform, zoneKey);

    private static PlayerBotRuntime NewBot(string name, string personality, float hpFraction = 1f)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.JoinActorWorld(session, actor);
        var c = actor.Character;
        c.Hp = (int)(c.MaxHp * hpFraction);
        var module = new ConflictJoinActivityModule(_ => new PlayerBotMetadata { Personality = personality });
        _ = module;
        return new PlayerBotRuntime(c, "conflict-rig");
    }

    private static BotActivityContext ContextFor(PlayerBotRuntime bot, ConflictJoinActivityModule module, string personality)
    {
        _ = module;
        _ = personality;
        return new BotActivityContext { Bot = bot, GameHour = 12f, ActiveActivity = null };
    }

    private static ConflictJoinActivityModule ModuleFor(string personality)
        => new(id => new PlayerBotMetadata { Personality = personality });

    private static TestableZoneConflict WarConflict()
    {
        var conflict = new TestableZoneConflict();
        conflict.ForceState(ZoneConflictType.War);
        conflict.ZoneGroupId = (ushort)TestZoneGroupId;
        return conflict;
    }

    private static TestableZoneConflict PeaceConflict()
    {
        var conflict = new TestableZoneConflict();
        conflict.ForceState(ZoneConflictType.Peace);
        conflict.ZoneGroupId = (ushort)TestZoneGroupId;
        return conflict;
    }

    [Test]
    public async Task Priority_Is75_BetweenRoamAndSchedule()
    {
        await Assert.That(new ConflictJoinActivityModule().Priority).IsEqualTo(75);
    }

    [Test]
    public async Task CanActivate_WarZoneReadyGuard_AllowsNamedActivity()
    {
        using var zone = new ZoneScope(WarConflict());
        var bot = NewBot("conf-war", "guard");
        zone.RegisterZone(bot.Character.Transform.ZoneId);
        var module = ModuleFor("guard");

        var first = module.CanActivate(ContextFor(bot, module, "guard"));
        var second = module.CanActivate(ContextFor(bot, module, "guard"));

        await Assert.That(first.CanActivate).IsTrue();
        await Assert.That(first.ActivityName).IsEqualTo($"conflict.{TestZoneGroupId}");
        await Assert.That(second.ActivityName).IsEqualTo(first.ActivityName);
    }

    [Test]
    public async Task CanActivate_PeaceZone_Denies()
    {
        using var zone = new ZoneScope(PeaceConflict());
        var bot = NewBot("conf-peace", "guard");
        zone.RegisterZone(bot.Character.Transform.ZoneId);
        var module = ModuleFor("guard");

        var decision = module.CanActivate(ContextFor(bot, module, "guard"));

        await Assert.That(decision.CanActivate).IsFalse();
    }

    [Test]
    public async Task CanActivate_DeadBot_Denies()
    {
        var conflict = new TestableZoneConflict();
        conflict.ForceState(ZoneConflictType.War);
        using var zone = new ZoneScope(conflict);
        var bot = NewBot("conf-dead", "guard", hpFraction: 0f);
        zone.RegisterZone(bot.Character.Transform.ZoneId);
        var module = ModuleFor("guard");

        var decision = module.CanActivate(ContextFor(bot, module, "guard"));

        await Assert.That(decision.CanActivate).IsFalse();
    }

    [Test]
    public async Task CanActivate_LowHp_Denies()
    {
        var conflict = new TestableZoneConflict();
        conflict.ForceState(ZoneConflictType.War);
        using var zone = new ZoneScope(conflict);
        var bot = NewBot("conf-lowhp", "guard", hpFraction: 0.5f);
        zone.RegisterZone(bot.Character.Transform.ZoneId);
        var module = ModuleFor("guard");

        var decision = module.CanActivate(ContextFor(bot, module, "guard"));

        await Assert.That(decision.CanActivate).IsFalse();
    }

    [Test]
    public async Task CanActivate_UnarmedUnskilled_Denies()
    {
        var conflict = new TestableZoneConflict();
        conflict.ForceState(ZoneConflictType.War);
        using var zone = new ZoneScope(conflict);
        var bot = NewBot("conf-unarmed", "guard");
        bot.Character.Skills.Skills.Clear();
        zone.RegisterZone(bot.Character.Transform.ZoneId);
        var module = ModuleFor("guard");

        var decision = module.CanActivate(ContextFor(bot, module, "guard"));

        await Assert.That(decision.CanActivate).IsFalse();
    }

    [Test]
    public async Task CanActivate_ReluctantPersonality_Denies()
    {
        var conflict = new TestableZoneConflict();
        conflict.ForceState(ZoneConflictType.War);
        using var zone = new ZoneScope(conflict);
        var bot = NewBot("conf-farmer", "farmer");
        zone.RegisterZone(bot.Character.Transform.ZoneId);
        var module = ModuleFor("farmer");

        var decision = module.CanActivate(ContextFor(bot, module, "farmer"));

        await Assert.That(decision.CanActivate).IsFalse();
    }

    [Test]
    public async Task CanActivate_SkilledButUnarmed_Allows()
    {
        var conflict = new TestableZoneConflict();
        conflict.ForceState(ZoneConflictType.War);
        using var zone = new ZoneScope(conflict);
        var bot = NewBot("conf-skilled", "pirate");
        bot.Character.Skills.AddSkill(new SkillTemplate { Id = 918181u }, 1, false);
        zone.RegisterZone(bot.Character.Transform.ZoneId);
        var module = ModuleFor("pirate");

        var decision = module.CanActivate(ContextFor(bot, module, "pirate"));

        await Assert.That(decision.CanActivate).IsTrue();
    }

    [Test]
    public async Task Activate_ReturnsStableConflictActivity()
    {
        using var zone = new ZoneScope(WarConflict());
        var bot = NewBot("conf-act", "guard");
        zone.RegisterZone(bot.Character.Transform.ZoneId);
        var module = ModuleFor("guard");

        var activity = module.Activate(ContextFor(bot, module, "guard"));

        await Assert.That(activity.Name).IsEqualTo($"conflict.{TestZoneGroupId}");
        await Assert.That(activity.ModuleName).IsEqualTo("ConflictJoin");
    }
}
