using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.UnitTests.Game.Quests.Scenario;

namespace AAEmu.UnitTests.Game.Quests.Playerbot;

/// <summary>Internal accessors so probe tests can reuse the pilot's helpers.</summary>
public static class PlayerbotPilotTestsAccess
{
    public static (PlayerBotController Bot, HeadlessSession Session) NewBot(string name, byte level = 1)
    {
        var session = HeadlessSession.Create((uint)(name.GetHashCode() & 0xFFFF), name, level, Race.Nuian);
        return (new PlayerBotController(session.Character), session);
    }

    public static QuestScenarioManifest LoadManifest(string tier, uint questId)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        var root = dir?.FullName ?? throw new InvalidOperationException("Cannot locate repo root");
        var path = Path.Combine(root, "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", tier, $"{questId}.json");
        return QuestScenarioManifest.LoadFromFile(path);
    }
}

[NotInParallel]
public class PilotProbeTests
{
    [Test]
    public async Task Probe_Quest251_RealTemplateVsManifest()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (bot, session) = PlayerbotPilotTestsAccess.NewBot("probe-251");
        var manifest = PlayerbotPilotTestsAccess.LoadManifest("t1", 251);

        var log = new System.Text.StringBuilder();
        log.AppendLine("=== manifest ===");
        foreach (var comp in manifest.Template.Components)
            log.AppendLine($"  comp kind={comp.Kind} id={comp.Id} acts=[{string.Join(", ", comp.Acts.Select(a => a.GetProperty("type").GetString()))}]");
        log.AppendLine($"  inventory: {string.Join(", ", manifest.Inventory.Select(i => $"{i.ItemId}x{i.Count}"))}");

        log.AppendLine("=== REAL template (QuestManager.Load) ===");
        var template = AAEmu.Game.Core.Managers.QuestManager.Instance.GetTemplate(251);
        foreach (var comp in template.Components.Values.OrderBy(c => c.KindId))
            log.AppendLine($"  comp kind={comp.KindId} id={comp.Id} acts=[{string.Join(", ", comp.ActTemplates.Select(a => a.GetType().Name))}]");

        // preseed + accept
        PlayerbotPilotRig.RegisterQuestItems(manifest);
        var bag = bot.Character.Inventory.Bag;
        log.AppendLine($"bag null? {bag == null}; freeSlots={bag?.FreeSlotCount}; size={bag?.ContainerSize}");
        log.AppendLine($"template 4058 registered? {AAEmu.Game.Core.Managers.ItemManager.Instance.GetTemplate(4058) != null}");
        var ok = bot.Character.Inventory.Bag.AcquireDefaultItem(AAEmu.Game.Models.Game.Items.Actions.ItemTaskType.QuestSupplyItems, 4058, 3);
        log.AppendLine($"AcquireDefaultItem(4058,3) returned: {ok}; bag.Items.Count={bag?.Items.Count}; GetItemsCount={bot.InventoryCount(4058)}");

        var accepted = bot.AcceptQuest(251, AAEmu.Game.Models.Game.Quests.Static.QuestAcceptorType.Npc, 3512);
        log.AppendLine($"accepted: {accepted}");
        var quest = bot.ActiveQuest(251);
        if (quest != null)
        {
            log.AppendLine($"after accept: step={quest.Step} status={quest.Status} objectives=[{string.Join(",", quest.Objectives)}] componentId={quest.ComponentId}");
            // second RunCurrentStep
            bot.Advance(251);
            log.AppendLine($"after 2nd advance: step={quest.Step} status={quest.Status} objectives=[{string.Join(",", quest.Objectives)}]");
        }
        File.WriteAllText("/tmp/pilot-probe-251.log", log.ToString());
        await Assert.That(accepted).IsTrue();
    }

    [Test]
    public async Task Probe_1959_AcceptRefused()
    {
        PlayerbotPilotRig.SeedPilotSingletons();
        var (bot, session) = PlayerbotPilotTestsAccess.NewBot("probe-1959");
        var manifest = PlayerbotPilotTestsAccess.LoadManifest("t4", 1959);
        var log = new System.Text.StringBuilder();

        // Real gate: the Start component requires CompleteQuestContext(1958).
        // A real character sees this quest with 1958 completed — rig the flag
        // through the engine's own API (same as the completion path).
        bot.Character.Quests.SetCompletedQuestFlag(1958, true);

        // unit reqs on the Start component
        var compsField = typeof(AAEmu.Game.Core.Managers.QuestManager).GetField("_componentTemplates",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        var comps = (Dictionary<uint, AAEmu.Game.Models.Game.Quests.QuestComponentTemplate>)compsField!.GetValue(AAEmu.Game.Core.Managers.QuestManager.Instance)!;
        foreach (var comp in comps.Values.Where(c => c.ParentQuestTemplate?.Id == 1959 && c.KindId == AAEmu.Game.Models.Game.Quests.Static.QuestComponentKind.Start))
        {
            log.AppendLine($"start comp {comp.Id}: orUnitReqs={comp.OrUnitReqs}");
            var reqs = AAEmu.Game.GameData.UnitRequirementsGameData.Instance.GetQuestComponentRequirements(comp.Id);
            foreach (var r in reqs)
                log.AppendLine($"  req kind={(AAEmu.Game.Models.Game.Units.Static.UnitReqsKindType)r.KindType} v1={r.Value1} v2={r.Value2}");
            log.AppendLine($"  CanComponentRun: {AAEmu.Game.GameData.UnitRequirementsGameData.Instance.CanComponentRun(comp, bot.Character)}");
            foreach (var act in comp.ActTemplates)
                log.AppendLine($"  act {act.GetType().Name} detailId={act.DetailId}");
        }

        PlayerbotPilotRig.RegisterQuestItems(manifest);
        foreach (var stockItem in manifest.Inventory)
            bot.StockInventory(stockItem.ItemId, stockItem.Count);
        log.AppendLine($"items: 8017={bot.InventoryCount(8017)} 15589={bot.InventoryCount(15589)}");
        var accepted = bot.AcceptQuest(1959, AAEmu.Game.Models.Game.Quests.Static.QuestAcceptorType.Item, 8017);
        log.AppendLine($"accept 1959 via Item/8017: {accepted}");
        var accepted2 = bot.AcceptQuest(1959, AAEmu.Game.Models.Game.Quests.Static.QuestAcceptorType.Unknown, 0);
        log.AppendLine($"accept 1959 via Unknown/0: {accepted2}");
        File.WriteAllText("/tmp/pilot-probe-1959.log", log.ToString());
        await Assert.That(accepted).IsTrue();
    }
}
