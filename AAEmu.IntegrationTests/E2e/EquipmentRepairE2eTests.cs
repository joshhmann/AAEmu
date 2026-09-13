using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

using Xunit;

namespace AAEmu.IntegrationTests.E2e;

/// <summary>
/// ECONOMY-REPAIR-01 live-stack proof, single-plane on the networked bot: a
/// REAL game server boots, the bot enters through the real login flow, the
/// mail-family rig stocks an equipment item with damaged durability, the bot
/// teleports to a live blacksmith spawner, and the mail-family repair op
/// runs the real GameplayActor.Repair path (Character.DoRepair).
/// Durability before/after via the mail inv op is the proof.
///
/// H stays UNKNOWN.
/// </summary>
[Collection("e2e")]
public class EquipmentRepairE2eTests
{
    private static readonly string NetName = "RepairNet" + Guid.NewGuid().ToString("N")[..8];
    private static readonly string NetAccount = "e2erepairnet" + Guid.NewGuid().ToString("N")[..8];
    private const uint BlacksmithTemplate = 10997;
    private const uint EquipTemplate = 5639;

    private static string EvidenceDir => Path.Combine(E2eStack.E2eRoot, "logs");
    private static string ReportPath => Path.Combine(EvidenceDir, "economy-repair-report.json");
    [Fact]
    [Trait("Category", "e2e")]
    public async Task Repair_DamagedGear_RestoresDurability()
    {
        var legs = new List<(string Leg, bool Passed, string Detail, double Ms)>();
        var wall = Stopwatch.StartNew();
        try
        {
            E2eStack.EnsureUp();
            E2eStack.RestartGameServer();

            using var net = await BotNetworkSession.ConnectAsync(
                NetName, NetAccount, "e2e-secret",
                "127.0.0.1", E2eStack.LoginPort,
                "127.0.0.1", E2eStack.GamePort,
                "127.0.0.1", E2eStack.StreamPort);
            Assert.True(net.InWorld, "net bot must be in-world");
            using var bridge = new BotDriveClient(E2eStack.BridgePort);

            // ------------------------------------------------ rig damaged gear + funds
            var rig = bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{NetName}\",\"op\":\"rig\",\"money\":100000,\"itemTemplate\":{EquipTemplate},\"count\":1,\"durability\":10}}", 30_000);
            Assert.True(rig.TryGetProperty("durability", out var dur0) && dur0.GetInt32() == 10,
                "rig must stock damaged gear: " + rig);
            var rigItemId = rig.TryGetProperty("itemId", out var rigItemEl) ? rigItemEl.GetUInt64() : 0UL;
            Assert.True(rigItemId != 0, "rig must return the stocked item id: " + rig);
            legs.Add(("rig-damaged", true, $"durability=10 itemId={rigItemId}", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ teleport to blacksmith spawner
            var teleport = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{NetName}\",\"op\":\"teleportToNpc\",\"npc\":{BlacksmithTemplate}}}", 30_000);
            Assert.True(teleport.TryGetProperty("x", out _), "teleport must resolve spawner: " + teleport);
            var smithObjId = 0u;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
            while (DateTime.UtcNow < deadline && smithObjId == 0)
            {
                var reply = bridge.Call($"{{\"cmd\":\"drive\",\"bot\":\"{NetName}\",\"op\":\"npcObjId\",\"npc\":{BlacksmithTemplate}}}", 30_000);
                if (reply.TryGetProperty("objId", out var id))
                    smithObjId = id.GetUInt32();
                if (smithObjId == 0)
                    await Task.Delay(1_000);
            }
            Assert.True(smithObjId != 0, "blacksmith must materialize");
            legs.Add(("blacksmith", true, $"smith objId {smithObjId}", wall.Elapsed.TotalMilliseconds));

            // ------------------------------------------------ repair through the real actor path
            var repair = bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{NetName}\",\"op\":\"repair\",\"npcObjId\":{smithObjId},\"itemId\":{rigItemId}}}", 60_000);
            Assert.True(repair.TryGetProperty("state", out var repairState) && repairState.GetString() == "Completed",
                "repair must complete: " + repair);
            legs.Add(("repair", true, "repair Completed", wall.Elapsed.TotalMilliseconds));
            // ------------------------------------------------ durability restored
            var after = bridge.Call($"{{\"cmd\":\"mail\",\"bot\":\"{NetName}\",\"op\":\"inv\"}}", 30_000);
            var items = after.GetProperty("items").EnumerateArray().ToList();
            var gear = items.FirstOrDefault(i => i.GetProperty("templateId").GetUInt32() == EquipTemplate);
            Assert.True(gear.ValueKind != JsonValueKind.Undefined, "gear must still be in bag: " + after);
            var durAfter = gear.GetProperty("durability").GetInt32();
            Assert.True(durAfter > 10, $"durability must rise above 10, got {durAfter}");
            legs.Add(("durability", true, $"durability 10 → {durAfter}", wall.Elapsed.TotalMilliseconds));

            await WriteReportAsync(true, legs, "repair live proof", wall.Elapsed.TotalSeconds);
        }
        catch (Exception ex)
        {
            legs.Add(("exception", false, ex.GetType().Name + ": " + ex.Message, wall.Elapsed.TotalMilliseconds));
            await WriteReportAsync(false, legs, ex.ToString(), wall.Elapsed.TotalSeconds);
            throw;
        }
    }

    private async Task WriteReportAsync(bool passed, List<(string Leg, bool Passed, string Detail, double Ms)> legs,
        string evidence, double wallSeconds)
    {
        Directory.CreateDirectory(EvidenceDir);
        var report = new JsonObject
        {
            ["test"] = "EconomyRepair",
            ["passed"] = passed,
            ["wallSeconds"] = wallSeconds,
            ["revision"] = E2eStack.SourceRevision,
            ["legs"] = new JsonArray(legs.Select(l => new JsonObject
            {
                ["leg"] = l.Leg,
                ["passed"] = l.Passed,
                ["detail"] = l.Detail,
                ["ms"] = l.Ms,
            }).ToArray()),
            ["evidence"] = evidence,
        };
        await File.WriteAllTextAsync(ReportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
