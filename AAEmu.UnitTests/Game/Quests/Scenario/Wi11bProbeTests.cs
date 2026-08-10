using System.Text;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests.Static;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>TEMP WI-11b probe (t_8ec705f0): diagnose 8000004 RESET refusal.</summary>
[NotInParallel]
public class Wi11bProbeTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, ".git")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException("Cannot locate repo root from " + AppContext.BaseDirectory);
    }

    [Test]
    public async Task Probe8000004Reset()
    {
        var sb = new StringBuilder();
        QuestScenarioDriver.SeedSingletons();
        var manifest = QuestScenarioManifest.LoadFromFile(Path.Combine(RepoRoot(),
            "AAEmu.UnitTests", "Game", "Quests", "Scenario", "Manifests", "t16", "8000004.json"));
        QuestScenarioDriver.RegisterManifestItems(manifest);
        var verdict = new QuestScenarioDriver().Run(manifest);
        sb.AppendLine("main run verdict: " + verdict.Overall);
        foreach (var s in verdict.Stages)
            sb.AppendLine($"  {s.Stage}: {s.Outcome} | {s.Reason}");
        var quest = verdict.QuestRef;
        var character = (Character)quest.Owner;
        sb.AppendLine($"ActiveQuests has 8000004: {character.Quests.ActiveQuests.ContainsKey(8000004)}");
        sb.AppendLine($"HasQuestCompleted(8000004): {character.Quests.HasQuestCompleted(8000004)}");
        var tpl = QuestManager.Instance.GetTemplate(8000004);
        sb.AppendLine($"GetTemplate(8000004) null: {tpl == null} | DetailId: {tpl?.DetailId} | Repeatable: {tpl?.Repeatable}");
        uint probeQid = 8000004;
        sb.AppendLine($"questBlockId math: {probeQid}/64 = {probeQid / 64} (ushort {(ushort)(probeQid / 64)}) | 125000*64+4 = {(uint)(125000 * 64) + 4}");
        character.Quests.ResetDailyQuests(true);
        sb.AppendLine($"after ResetDailyQuests -> HasQuestCompleted(8000004): {character.Quests.HasQuestCompleted(8000004)}");
        var re = character.Quests.AddQuest(8000004, false, QuestAcceptorType.Npc, 10857);
        sb.AppendLine($"re-accept via engine AddQuest: {re} | active now: {character.Quests.ActiveQuests.ContainsKey(8000004)}");
        if (re)
        {
            var q2 = character.Quests.ActiveQuests[8000004];
            sb.AppendLine($"re-accepted quest Step={q2.Step} Status={q2.Status} acceptor={q2.QuestAcceptorType}/{q2.AcceptorId}");
        }
        File.WriteAllText("/tmp/wi11b-probe.txt", sb.ToString());
        await Assert.That(true).IsTrue();
    }
}
