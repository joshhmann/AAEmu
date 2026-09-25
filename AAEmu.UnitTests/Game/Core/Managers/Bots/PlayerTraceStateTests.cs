using System.Text.Json;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.UnitTests.Game.Quests.Playerbot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Trace schema v2 — the consequence state snapshot. Asserts the observable
/// contract of the trace FILE (what a consumer parses), not the mechanism:
///
///  • outcome events (mount, loot grant, quest accept) carry a `state` block
///    whose values match the live server state at event time;
///  • the bag delta of a loot grant is the exact item-unit change that grant
///    produced;
///  • every state field is present on consequence rows — unreadable ones as
///    JSON null, so "unknown" is distinguishable from "key missing";
///  • non-consequence rows (lifecycle/marker) keep the v1 shape (no `state`
///    key), so old consumers are unaffected.
///
/// Real headless actor through the real engine paths — no fakes.
/// </summary>
[ParallelLimiter<PlayerTraceSequentialParallelLimit>]
[NotInParallel]
public class PlayerTraceStateTests
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    /// <summary>Every state field a consequence row must spell out.</summary>
    private static readonly string[] RequiredStateFields =
    [
        "hp", "maxHp", "mp", "maxMp", "money", "labor", "bagTotal", "bagSlots",
        "bagFreeSlots", "bagDelta", "questId", "questActive", "questStep",
        "questStatus", "questObjectives", "targetObjId", "isMounted", "pos"
    ];

    [Before(Test)]
    public void SetUp()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        PlayerbotPilotRig.SeedPilotSingletons();
        GameplayActorTestRig.Seed();
    }

    [Test]
    public async Task MountOutcome_StateRecordsIsMountedAndVitals()
    {
        await Gate.WaitAsync();
        var service = PlayerTraceService.Instance;
        var (actor, session) = GameplayActorTestRig.CreateActor("trace-state-mount");
        var trace = Start(service, actor.Character.Id);
        try
        {
            var mateObjId = GameplayActorTestRig.SummonMate(session, actor);
            actor.Character.Money = 4321;

            // MountMate is the same engine entry the CSMountMatePacket handler
            // drives, so the mount_mate row comes from the real mount path.
            var request = actor.Mount(mateObjId);
            await Assert.That(request.IsTerminal).IsTrue();

            var rows = ReadRows(trace.Path, "mount_mate");
            await Assert.That(rows.Count).IsGreaterThanOrEqualTo(1);
            var state = rows[0].State;
            await Assert.That(state).IsNotNull();
            await Assert.That(state!.IsMounted).IsTrue();
            await Assert.That(state.Money).IsEqualTo(4321);
            await Assert.That(state.Hp).IsEqualTo(actor.Character.Hp);
            await Assert.That(state.MaxHp).IsEqualTo(actor.Character.MaxHp);
            await Assert.That(state.Pos).IsNotNull();
        }
        finally
        {
            StopAndDelete(service, trace);
            Gate.Release();
        }
    }

    [Test]
    public async Task LootGrant_StateCarriesExactBagDelta()
    {
        await Gate.WaitAsync();
        var service = PlayerTraceService.Instance;
        var (actor, session) = GameplayActorTestRig.CreateActor("trace-state-loot");
        var trace = Start(service, actor.Character.Id);
        try
        {
            const uint itemTemplate = 91_412;
            GameplayActorTestRig.SeedItemTemplate(itemTemplate);
            var npcObjId = GameplayActorTestRig.SpawnNpc(session, 3475);
            var npc = session.World.GetNpc(npcObjId)!;
            GameplayActorTestRig.SeedLootContainer(npc, (itemTemplate, 3));

            var request = actor.Loot(npcObjId);
            await Assert.That(request.IsTerminal).IsTrue();

            var rows = ReadRows(trace.Path, "loot_granted");
            await Assert.That(rows.Count).IsGreaterThanOrEqualTo(1);
            var state = rows[0].State;
            await Assert.That(state).IsNotNull();
            // The exact item-unit delta this grant produced.
            await Assert.That(state!.BagDelta).IsEqualTo(3);
            await Assert.That(state.BagSlots).IsNotNull();
            await Assert.That(state.BagFreeSlots).IsNotNull();
        }
        finally
        {
            StopAndDelete(service, trace);
            Gate.Release();
        }
    }

    [Test]
    public async Task QuestAccept_StateCarriesQuestIdentityAndObjectives()
    {
        await Gate.WaitAsync();
        var service = PlayerTraceService.Instance;
        const uint questId = 91_421;
        GameplayActorTestRig.SeedQuestDelivery(questId, 91_422, 91_423, 91_424, 91_425, level: 1);
        var (actor, _) = GameplayActorTestRig.CreateActor("trace-state-quest");
        var trace = Start(service, actor.Character.Id);
        try
        {
            var accepted = actor.AcceptQuest(questId, QuestAcceptorType.Npc, 91_424);
            await Assert.That(accepted.IsTerminal).IsTrue();

            var rows = ReadRows(trace.Path, "quest_accepted");
            await Assert.That(rows.Count).IsGreaterThanOrEqualTo(1);
            var state = rows[0].State;
            await Assert.That(state).IsNotNull();
            await Assert.That(state!.QuestId).IsEqualTo(questId);
            await Assert.That(state.QuestActive).IsTrue();
            await Assert.That(state.QuestStep).IsNotNull();
            await Assert.That(state.QuestStatus).IsNotNull();
            await Assert.That(state.QuestObjectives).IsNotNull();
        }
        finally
        {
            StopAndDelete(service, trace);
            Gate.Release();
        }
    }

    [Test]
    public async Task OutcomeRowsSpellOutEveryField_NonOutcomeRowsKeepV1Shape()
    {
        await Gate.WaitAsync();
        var service = PlayerTraceService.Instance;
        var (actor, _) = GameplayActorTestRig.CreateActor("trace-state-shape");
        var trace = Start(service, actor.Character.Id);
        try
        {
            service.RecordSkill(actor.Character, "skill_requested", 16064);
            service.RecordMark("checkpoint");

            var lines = await File.ReadAllLinesAsync(trace.Path);
            var sawOutcome = false;
            var sawLifecycle = false;
            var sawMarker = false;
            foreach (var line in lines)
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var category = root.GetProperty("category").GetString();
                var hasState = root.TryGetProperty("state", out var state);

                switch (category)
                {
                    case "lifecycle":
                        sawLifecycle = true;
                        await Assert.That(hasState).IsFalse();
                        break;
                    case "marker":
                        sawMarker = true;
                        await Assert.That(hasState).IsFalse();
                        break;
                    case "skill":
                        // Outcome rows spell out EVERY field — unknown as JSON
                        // null, never an absent key.
                        await Assert.That(hasState).IsTrue();
                        foreach (var field in RequiredStateFields)
                            await Assert.That(state.TryGetProperty(field, out _)).IsTrue();
                        sawOutcome = true;
                        break;
                }
            }

            await Assert.That(sawLifecycle).IsTrue();
            await Assert.That(sawMarker).IsTrue();
            await Assert.That(sawOutcome).IsTrue();
        }
        finally
        {
            StopAndDelete(service, trace);
            Gate.Release();
        }
    }

    // ------------------------------------------------------------- fixture

    private static (string Path, uint CharacterId) Start(PlayerTraceService service, uint characterId)
    {
        if (service.IsActive)
            service.StopTrace();
        var (started, message, path) = service.StartTrace(characterId, "trace-state-bot", "trace_state_unit");
        if (!started || path == null)
            throw new InvalidOperationException($"trace did not start: {message}");
        return (path, characterId);
    }

    /// <summary>Deserializes the rows with the requested event name from the trace file.</summary>
    private static List<PlayerTraceRecord> ReadRows(string path, string eventName)
    {
        var rows = new List<PlayerTraceRecord>();
        foreach (var line in File.ReadAllLines(path))
        {
            var record = JsonSerializer.Deserialize<PlayerTraceRecord>(line);
            if (record?.Event == eventName)
                rows.Add(record);
        }
        return rows;
    }

    private static void StopAndDelete(PlayerTraceService service, (string Path, uint CharacterId) trace)
    {
        if (service.IsActive)
            service.StopTrace();
        if (File.Exists(trace.Path))
            File.Delete(trace.Path);
    }
}
