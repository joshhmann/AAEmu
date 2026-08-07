using System.Text.Json;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// Minimal reader for the committed scenario manifests
/// (AAEmu.UnitTests/Game/Quests/Scenario/Manifests/t1/*.json) — only the
/// fields the E2E quest drive needs. The manifests are the same calibrated
/// drive specs the pilot uses; the E2E runner drives them over the bridge
/// against the live server instead of in-process.
/// </summary>
public sealed class E2eQuestManifest
{
    public uint QuestId { get; init; }
    public string Name { get; init; } = "";
    public int Level { get; init; }
    public string AcceptorType { get; init; } = "Npc";
    public uint AcceptorId { get; init; }
    public int SelectedRewardIndex { get; init; } = -1;
    public List<(uint ItemId, int Count)> Inventory { get; init; } = [];
    public List<Stage> Stages { get; init; } = [];

    public sealed class Stage
    {
        public string Name { get; set; } = "";
        public List<JsonElement> Events { get; set; } = [];
        public string? ExpectStep { get; set; }
        public string? ExpectStatus { get; set; }
        public bool? ExpectCompleted { get; set; }
        public int[]? ExpectObjectives { get; set; }
        public List<(uint ItemId, int Count)> ExpectRewardItems { get; set; } = [];
    }

    public static E2eQuestManifest LoadFromFile(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var stages = new List<Stage>();
        if (root.TryGetProperty("stages", out var stagesEl))
        {
            foreach (var s in stagesEl.EnumerateArray())
            {
                var stage = new Stage { Name = s.GetProperty("name").GetString() ?? "" };
                if (s.TryGetProperty("events", out var eventsEl))
                {
                    foreach (var ev in eventsEl.EnumerateArray())
                        stage.Events.Add(ev.Clone());
                }

                if (s.TryGetProperty("expect", out var expect))
                {
                    if (expect.TryGetProperty("step", out var step))
                        stage.ExpectStep = step.GetString();
                    if (expect.TryGetProperty("status", out var status))
                        stage.ExpectStatus = status.GetString();
                    if (expect.TryGetProperty("completed", out var completed))
                        stage.ExpectCompleted = completed.GetBoolean();
                    if (expect.TryGetProperty("objectives", out var objectives))
                    {
                        var list = new List<int>();
                        foreach (var o in objectives.EnumerateArray())
                            list.Add(o.GetInt32());
                        stage.ExpectObjectives = list.ToArray();
                    }

                    if (expect.TryGetProperty("rewardItems", out var rewardItems))
                    {
                        var items = new List<(uint, int)>();
                        foreach (var ri in rewardItems.EnumerateArray())
                            items.Add((ri.GetProperty("itemId").GetUInt32(), ri.GetProperty("count").GetInt32()));
                        stage.ExpectRewardItems = items;
                    }
                }

                stages.Add(stage);
            }
        }

        var inventory = new List<(uint, int)>();
        if (root.TryGetProperty("inventory", out var invEl))
        {
            foreach (var i in invEl.EnumerateArray())
                inventory.Add((i.GetProperty("itemId").GetUInt32(), i.GetProperty("count").GetInt32()));
        }

        return new E2eQuestManifest
        {
            QuestId = root.GetProperty("questId").GetUInt32(),
            Name = root.TryGetProperty("name", out var name) ? name.GetString() ?? "" : "",
            Level = root.TryGetProperty("level", out var level) ? level.GetInt32() : 1,
            AcceptorType = root.GetProperty("acceptor").GetProperty("type").GetString() ?? "Npc",
            AcceptorId = root.GetProperty("acceptor").GetProperty("id").GetUInt32(),
            SelectedRewardIndex = root.TryGetProperty("selectedRewardIndex", out var sel) ? sel.GetInt32() : -1,
            Inventory = inventory,
            Stages = stages
        };
    }
}
