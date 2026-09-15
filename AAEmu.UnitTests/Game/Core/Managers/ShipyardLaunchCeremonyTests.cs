using System.Collections;
using System.Reflection;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Containers;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using TUnit.Core.Interfaces;
using AaEmuTask = AAEmu.Game.Models.Tasks.Task;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// Launch-ceremony strand guard: a frame finished purely through Craft-group
/// build-skill hits used to strand at step -1, because the scroll grant +
/// ceremony lived only in CraftEffect's default (ungrouped-wi) branch, which a
/// Craft-group hit never reaches. These tests drive CraftEffect.Apply with a
/// Craft-group wi and assert the ceremony fires exactly once.
///
/// Suite-isolation discipline: the grant path resolves the SHARED
/// ItemManager/WorldManager/QuestManager singletons, which bot-rig suites
/// replace unconditionally per test (QuestScenarioDriver/SeedPilotSingletons)
/// while running in parallel with this class. Every attempt therefore
/// re-acquires the CURRENT instances, re-registers the scroll template,
/// installs a private item-id source, and uses a pristine owner, frame, and
/// mocks; attempts never contaminate each other. A bounded retry absorbs a
/// swap landing mid-Apply; a genuinely broken SUT fails every attempt
/// identically, so the tests still fail (verified fail-pre).
/// </summary>
[NotInParallel]
[ParallelLimiter<ShipyardCeremonySequentialLimit>]
public class ShipyardLaunchCeremonyTests
{
    private const string OwnerName = "shipwright";
    private const uint BuildSkillId = 17002;
    private const uint ScrollTemplateId = 95001;
    private const WorldInteractionType BuildWi = (WorldInteractionType)9182;
    private const int MaxAttempts = 3;

    // Unique-per-attempt owner ids, inside the range the cleanup hook below
    // removes. Fixed ids are unsafe: a concurrent suite run may hold
    // same-id containers, and GetItemContainerForCharacter first-wins by
    // (OwnerId, ContainerType).
    private static int s_nextOwnerId = 910100;
    private const uint RigOwnerIdBase = 900000;

    // Private item-id range: 0x01/0x02 are taken by other rigs. Issuing from
    // our own range means Create can never collide with ambient _allItems
    // entries left behind by counter resets elsewhere in the suite.
    private static int s_nextItemId = 0x0A000000;

    [After(Test)]
    public void AfterTest_CleanupFixtureContainers()
    {
        if (typeof(Singleton<ItemManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)?.GetValue(null) == null)
            return;
        var itemManager = ItemManager.Instance;
        var containersField = typeof(ItemManager).GetField("_allPersistentContainers", BindingFlags.NonPublic | BindingFlags.Instance);
        var itemsField = typeof(ItemManager).GetField("_allItems", BindingFlags.NonPublic | BindingFlags.Instance);
        if (containersField?.GetValue(itemManager) is not IDictionary containers)
            return;
        if (itemsField?.GetValue(itemManager) is not IDictionary allItems)
            return;

        var toRemove = new List<object>();
        foreach (DictionaryEntry entry in containers)
        {
            if (entry.Value is not ItemContainer container || container.OwnerId < RigOwnerIdBase)
                continue;
            toRemove.Add(entry.Key);
            foreach (var item in container.Items.ToList())
                allItems.Remove(item.Id);
        }
        foreach (var key in toRemove)
            containers.Remove(key);
    }

    [Test]
    public async Task OwnerFinishingHit_StartsCeremonyExactlyOnce()
    {
        GameplayActorTestRig.Seed();
        using var scope = RunUntilComplete(
            () => CreateScope(actionsPerStep: 1, includeStranger: false),
            s => ApplyBuildHit(s.Owner, s.Frame),
            s => s.ScrollCount == 1 && s.Frame.ShipyardData.Step == 1000);

        // The finishing Craft-group hit: pre-fix this stranded the frame
        // (state broadcast, no scroll, nothing scheduled).
        await Assert.That(scope.ScrollCount).IsEqualTo(1);
        await Assert.That(scope.Frame.ShipyardData.Step).IsEqualTo(1000);
        scope.Tasks.Schedule(Any<AaEmuTask>(), Any<TimeSpan?>(), Any<TimeSpan?>(), Any<int>()).WasCalled(Times.Once);

        // A follow-up interaction landing in the default branch reaches the
        // same completion path — it must not grant the scroll twice. The
        // Step sentinel makes both repeat calls no-ops before any singleton
        // is touched, so this half is immune to suite swaps.
        scope.Manager.ShipyardCompletedTask(scope.Frame);
        ApplyBuildHit(scope.Owner, scope.Frame);

        await Assert.That(scope.ScrollCount).IsEqualTo(1);
        scope.Tasks.Schedule(Any<AaEmuTask>(), Any<TimeSpan?>(), Any<TimeSpan?>(), Any<int>()).WasCalled(Times.Once);
    }

    [Test]
    public async Task UnfinishedHit_ReportsProgressWithoutCeremony()
    {
        GameplayActorTestRig.Seed();
        // One step needing two actions: a single hit stays on step 0, the frame is not done.
        using var scope = RunUntilComplete(
            () => CreateScope(actionsPerStep: 2, includeStranger: false),
            s => ApplyBuildHit(s.Owner, s.Frame),
            s => s.Frame.CurrentStep == 0 && s.Frame.ShipyardData.Step == 0 && s.ScrollCount == 0);
        await Assert.That(scope.Frame.CurrentStep).IsEqualTo(0);
        await Assert.That(scope.Frame.ShipyardData.Step).IsEqualTo(0);
        await Assert.That(scope.ScrollCount).IsEqualTo(0);
        scope.Tasks.Schedule(Any<AaEmuTask>(), Any<TimeSpan?>(), Any<TimeSpan?>(), Any<int>()).WasCalled(Times.Never);
    }

    [Test]
    public async Task StrangerFinishingHit_DoesNotStartCeremony()
    {
        GameplayActorTestRig.Seed();
        using var scope = RunUntilComplete(
            () => CreateScope(actionsPerStep: 1, includeStranger: true),
            s => ApplyBuildHit(s.Stranger!, s.Frame),
            s => s.ScrollCount == 0 && s.Frame.ShipyardData.Step != 1000);

        await Assert.That(scope.ScrollCount).IsEqualTo(0);
        await Assert.That(scope.Frame.ShipyardData.Step).IsNotEqualTo(1000);
        scope.Tasks.Schedule(Any<AaEmuTask>(), Any<TimeSpan?>(), Any<TimeSpan?>(), Any<int>()).WasCalled(Times.Never);
    }
    private static AttemptScope RunUntilComplete(Func<AttemptScope> create, Action<AttemptScope> act, Func<AttemptScope, bool> isComplete)
    {
        AttemptScope? scope = null;
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                var last = attempt + 1 >= MaxAttempts;
                scope?.Dispose();
                scope = null;
                if (!last)
                {
                    try
                    {
                        scope = create();
                        act(scope);
                    }
                    catch
                    {
                        continue;
                    }
                    if (isComplete(scope))
                        return scope;
                }
                else
                {
                    // Final attempt: exceptions and state assert as-is, so a
                    // genuinely broken SUT still fails instead of retrying.
                    scope = create();
                    act(scope);
                    return scope;
                }
            }
        }
        catch
        {
            scope?.Dispose();
            throw;
        }
    }

    private static void ApplyBuildHit(Character caster, Shipyard shipyard)
    {
        var effect = new CraftEffect { WorldInteraction = BuildWi };
        effect.Apply(caster, null!, shipyard, new SkillCastUnitTarget(), new CastSkill(BuildSkillId, 0),
            new EffectSource { Skill = new Skill() }, null!, DateTime.UtcNow);
    }

    private static AttemptScope CreateScope(int actionsPerStep, bool includeStranger)
    {
        GameplayActorTestRig.Seed();
        var scope = new AttemptScope();
        try
        {
            scope.EnsureGrantPath();
            scope.EnsureCraftWiGroup();
            scope.Owner = BuildCharacter((uint)Interlocked.Increment(ref s_nextOwnerId), OwnerName);
            if (includeStranger)
                scope.Stranger = BuildCharacter((uint)Interlocked.Increment(ref s_nextOwnerId), "bystander");
            scope.Frame = BuildShipyard(actionsPerStep);
            (scope.Manager, scope.Tasks) = BuildManager(scope.Owner);
            scope.SwapShipyardManager();
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    private static (ShipyardManager Manager, Mock<ITaskManager> TaskManager) BuildManager(Character owner)
    {
        var taskManager = Mock.Of<ITaskManager>();
        taskManager.Schedule(Any<AaEmuTask>(), Any<TimeSpan?>(), Any<TimeSpan?>(), Any<int>()).Returns(true);
        var worldManager = Mock.Of<IWorldManager>();
        worldManager.GetCharacter(OwnerName).Returns(owner);
        var manager = new ShipyardManager(
            taskManager.Object,
            Mock.Of<IObjectIdManager>().Object,
            Mock.Of<IShipyardIdManager>().Object,
            worldManager.Object,
            Mock.Of<ITaxationsManager>().Object,
            Mock.Of<ISkillManager>().Object,
            Mock.Of<IShipyardFrameStore>().Object);
        return (manager, taskManager);
    }

    private static Character BuildCharacter(uint id, string name)
    {
        var character = new Character(new UnitCustomModelParams()) { Id = id, Name = name, NumInventorySlots = 50, NumBankSlots = 50 };
        character.Inventory = new Inventory(character);
        character.Craft = new CharacterCraft(character);
        return character;
    }

    private static Shipyard BuildShipyard(int actionsPerStep = 1)
    {
        var steps = new Dictionary<int, ShipyardSteps>
        {
            [0] = new ShipyardSteps { Id = 1, ShipyardId = 91, Step = 0, ModelId = 901, SkillId = BuildSkillId, NumActions = actionsPerStep, MaxHp = 100 }
        };
        // Single step: one build hit finishes the frame when actionsPerStep is 1.
        var template = new ShipyardsTemplate
        {
            Id = 91,
            Name = "RigClipper",
            MainModelId = 900,
            ItemId = ScrollTemplateId,
            CeremonyAnimTime = 12000,
            ShipyardSteps = steps
        };
        return new Shipyard
        {
            Template = template,
            ShipyardData = new ShipyardData { Id = 91, TemplateId = template.Id, OwnerName = OwnerName, Step = 0, Actions = 0 }
        };
    }

    private sealed class AttemptScope : IDisposable
    {
        public Character Owner { get; set; } = null!;
        public Character? Stranger { get; set; }
        public Shipyard Frame { get; set; } = null!;
        public ShipyardManager Manager { get; set; } = null!;
        public Mock<ITaskManager> Tasks { get; set; } = null!;

        public int ScrollCount => Owner.Inventory.GetItemsCount(SlotType.Inventory, ScrollTemplateId);

        private readonly List<Action> _restore = [];

        public void EnsureGrantPath()
        {
            var itemManager = ItemManager.Instance;
            const BindingFlags flags = BindingFlags.NonPublic | BindingFlags.Instance;
            var templates = (Dictionary<uint, ItemTemplate>)typeof(ItemManager).GetField("_templates", flags)!.GetValue(itemManager)!;
            templates.TryAdd(ScrollTemplateId, new ItemTemplate { Id = ScrollTemplateId, MaxCount = 100 });

            var idField = typeof(ItemManager).GetField("<itemIdManager>P", flags) ?? typeof(ItemManager).GetField("itemIdManager", flags)!;
            var prior = (IItemIdManager)idField.GetValue(itemManager)!;
            var mock = Mock.Of<IItemIdManager>();
            mock.GetNextId().Returns(() => (uint)Interlocked.Increment(ref s_nextItemId));
            idField.SetValue(itemManager, mock.Object);
            _restore.Add(() => idField.SetValue(itemManager, prior));
        }

        public void EnsureCraftWiGroup()
        {
            var worldManager = WorldManager.Instance;
            var groups = (Dictionary<uint, WorldInteractionGroup>)typeof(WorldManager)
                .GetField("_worldInteractionGroups", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(worldManager)!;
            groups[(uint)BuildWi] = WorldInteractionGroup.Craft;
            _restore.Add(() => groups.Remove((uint)BuildWi));
        }

        public void SwapShipyardManager()
        {
            var field = typeof(Singleton<ShipyardManager>).GetField("s_instance", BindingFlags.NonPublic | BindingFlags.Static)!;
            var previous = field.GetValue(null);
            field.SetValue(null, Manager);
            _restore.Add(() => field.SetValue(null, previous));
        }

        public void Dispose()
        {
            for (var i = _restore.Count - 1; i >= 0; i--)
                _restore[i]();
            _restore.Clear();
        }
    }
}

/// <summary>Serializes <see cref="ShipyardLaunchCeremonyTests"/>: attempts within
/// a test share the ShipyardManager singleton swap and must not run in parallel.</summary>
public sealed class ShipyardCeremonySequentialLimit : IParallelLimit
{
    public int Limit => 1;
}
