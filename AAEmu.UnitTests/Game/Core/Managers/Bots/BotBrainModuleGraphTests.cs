using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Offline validation of the declared decision-layer brain module graph: the
/// unknown-name, duplicate and cycle refusals, plus the production table itself
/// (the combat→travel and loot→travel edges are the coupling
/// <c>CombatTravelDispatch.Prepare</c> / <c>LootTravelDispatch.Prepare</c> create
/// against <c>TravelBrainPlanner.Prepare</c>). Pure data in, ordering or a named
/// <see cref="BotBrainModuleGraphException"/> out — no engine, no world, no rig.
/// </summary>
public class BotBrainModuleGraphTests
{
    // ------------------------------------------------------------- production table

    [Test]
    public async Task Validate_ProductionModules_OrdersDependenciesFirst()
    {
        var order = BotBrainModuleGraph.Validate();

        // travel is the root: both consumers require it, so it must precede them.
        var travel = order.ToList().IndexOf("travel");
        var combat = order.ToList().IndexOf("combat");
        var loot = order.ToList().IndexOf("loot");

        await Assert.That(order.Count).IsEqualTo(3);
        await Assert.That(travel).IsGreaterThanOrEqualTo(0);
        await Assert.That(travel).IsLessThan(combat);
        await Assert.That(travel).IsLessThan(loot);
    }

    [Test]
    public async Task Validate_ProductionModules_DeclaresTheCombatAndLootTravelEdges()
    {
        var declared = BotBrainModuleGraph.Modules.ToDictionary(m => m.Name, m => m.Requires);

        await Assert.That(declared["combat"].ToList()).IsEquivalentTo(["travel"]);
        await Assert.That(declared["loot"].ToList()).IsEquivalentTo(["travel"]);
        await Assert.That(declared["travel"]).IsEmpty();
    }

    // ------------------------------------------------------------- unknown names

    [Test]
    public async Task Validate_UnknownDependency_ThrowsNamedError()
    {
        BotBrainModule[] modules =
        [
            new("combat", ["swim"]),
            new("travel", [])
        ];

        var ex = Assert.Throws<BotBrainModuleGraphException>(() => BotBrainModuleGraph.Validate(modules));

        await Assert.That(ex.Error).IsEqualTo(BotBrainModuleGraphError.UnknownDependency);
        await Assert.That(ex.Message).Contains("UNKNOWN-DEPENDENCY");
        await Assert.That(ex.Message).Contains("module=combat");
        await Assert.That(ex.Message).Contains("dep=swim");
    }

    // ------------------------------------------------------------- duplicates

    [Test]
    public async Task Validate_DuplicateModuleName_ThrowsNamedError()
    {
        BotBrainModule[] modules =
        [
            new("travel", []),
            new("travel", [])
        ];

        var ex = Assert.Throws<BotBrainModuleGraphException>(() => BotBrainModuleGraph.Validate(modules));

        await Assert.That(ex.Error).IsEqualTo(BotBrainModuleGraphError.DuplicateModule);
        await Assert.That(ex.Message).Contains("DUPLICATE-MODULE");
        await Assert.That(ex.Message).Contains("module=travel");
    }

    [Test]
    public async Task Validate_DuplicateDependencyInOneModule_ThrowsNamedError()
    {
        BotBrainModule[] modules =
        [
            new("combat", ["travel", "travel"]),
            new("travel", [])
        ];

        var ex = Assert.Throws<BotBrainModuleGraphException>(() => BotBrainModuleGraph.Validate(modules));

        await Assert.That(ex.Error).IsEqualTo(BotBrainModuleGraphError.DuplicateDependency);
        await Assert.That(ex.Message).Contains("DUPLICATE-DEPENDENCY");
        await Assert.That(ex.Message).Contains("module=combat");
    }

    // ------------------------------------------------------------- cycles

    [Test]
    public async Task Validate_DirectCycle_ThrowsNamedError()
    {
        BotBrainModule[] modules =
        [
            new("combat", ["combat"])
        ];

        var ex = Assert.Throws<BotBrainModuleGraphException>(() => BotBrainModuleGraph.Validate(modules));

        await Assert.That(ex.Error).IsEqualTo(BotBrainModuleGraphError.DependencyCycle);
        await Assert.That(ex.Message).Contains("CYCLE");
        await Assert.That(ex.Message).Contains("modules=combat");
    }

    [Test]
    public async Task Validate_TransitiveCycle_ThrowsNamedError()
    {
        // combat → loot → travel → combat: no module is placeable first.
        BotBrainModule[] modules =
        [
            new("combat", ["loot"]),
            new("loot", ["travel"]),
            new("travel", ["combat"])
        ];

        var ex = Assert.Throws<BotBrainModuleGraphException>(() => BotBrainModuleGraph.Validate(modules));

        await Assert.That(ex.Error).IsEqualTo(BotBrainModuleGraphError.DependencyCycle);
        await Assert.That(ex.Message).Contains("CYCLE");
        // The acyclic module alongside the cycle is placed; the cycle names itself.
        await Assert.That(ex.Message).Contains("combat");
        await Assert.That(ex.Message).Contains("loot");
        await Assert.That(ex.Message).Contains("travel");
    }

    [Test]
    public async Task Validate_PlacesAcyclicModulesBeforeReportingTheCycle()
    {
        // survival has no edge, so it is placeable and must NOT be named in the
        // cycle report — the refusal names the unorderable set, not the table.
        BotBrainModule[] modules =
        [
            new("survival", []),
            new("combat", ["travel"]),
            new("travel", ["combat"])
        ];

        var ex = Assert.Throws<BotBrainModuleGraphException>(() => BotBrainModuleGraph.Validate(modules));

        await Assert.That(ex.Error).IsEqualTo(BotBrainModuleGraphError.DependencyCycle);
        await Assert.That(ex.Message).DoesNotContain("survival");
    }

    // ------------------------------------------------------------- argument guard

    [Test]
    public async Task Validate_NullModules_Throws()
    {
        var ex = Assert.Throws<ArgumentNullException>(() => BotBrainModuleGraph.Validate(null!));

        await Assert.That(ex.ParamName).IsEqualTo("modules");
    }
}
