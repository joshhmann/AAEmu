using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Quests.Acts;
using AAEmu.Game.Models.Game.Quests.Static;
using AAEmu.Game.Models.Game.Quests.Templates;
using AAEmu.Game.Models.Game.World;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.UnitTests.Game.Models.Game.Bots;

[NotInParallel]
public class BotDriveBridgeQuestObserveTests
{
    private const uint QuestId = 918251;
    private const uint GatherComponentId = 918633;
    private const uint OtherComponentId = 918638;
    private const uint GatherActId = 918473;
    private const uint HuntActId = 918474;
    private const uint ItemId = 4058;
    private const BindingFlags PrivateInstance = BindingFlags.NonPublic | BindingFlags.Instance;

    [Test]
    public async Task PassiveObserve_ProgressGatherIndexZero_AllowsDifferentKindObjectiveIndexZero()
    {
        await WithObserverAsync(async fixture =>
        {
            // A successful call to the SAME real passive entry is required in
            // each regression: DI/session/serialization failures are not RED.
            using var baseline = fixture.Observe();
            await AssertValidGatherAsync(baseline.RootElement, fixture);
            fixture.AddHunt(QuestComponentKind.Ready);
            await Assert.That(fixture.Gather.ThisComponentObjectiveIndex).IsEqualTo((byte)0);
            await Assert.That(fixture.Hunt!.ThisComponentObjectiveIndex).IsEqualTo((byte)0);
            await Assert.That(fixture.Hunt.ParentComponent.KindId).IsEqualTo(QuestComponentKind.Ready);
            var before = fixture.Quest.WriteData();

            using var observed = fixture.Observe();
            await AssertValidGatherAsync(observed.RootElement, fixture);
            await Assert.That(fixture.Quest.WriteData().SequenceEqual(before)).IsTrue();
            await Assert.That(fixture.Scheduler.IsRunning).IsFalse();
            await Assert.That(fixture.Manager.TryGet(fixture.Session.Character.Id, out _)).IsFalse();
        });
    }

    [Test]
    public async Task PassiveObserve_ProgressGatherIndexZero_SameProgressObjectiveIndexZero_LeavesTheQuestUnmapped()
    {
        await WithObserverAsync(async fixture =>
        {
            using var baseline = fixture.Observe();
            await AssertValidGatherAsync(baseline.RootElement, fixture);
            fixture.AddHunt(QuestComponentKind.Progress);
            await Assert.That(fixture.Gather.ThisComponentObjectiveIndex).IsEqualTo((byte)0);
            await Assert.That(fixture.Hunt!.ThisComponentObjectiveIndex).IsEqualTo((byte)0);
            await Assert.That(fixture.Hunt.ParentComponent.KindId).IsEqualTo(QuestComponentKind.Progress);
            var before = fixture.Quest.WriteData();

            // An ambiguous objective is never mapped, and the quest is named
            // instead of refusing the whole observation (T10 run 17).
            using var observed = fixture.Observe();
            await AssertUnmappedAsync(observed.RootElement);
            await Assert.That(fixture.Quest.WriteData().SequenceEqual(before)).IsTrue();
            await Assert.That(fixture.Scheduler.IsRunning).IsFalse();
            await Assert.That(fixture.Manager.TryGet(fixture.Session.Character.Id, out _)).IsFalse();
        });
    }

    [Test]
    public async Task PassiveObserve_AuditCursorRetainedInclusive_ReturnsOverlapAndAllLaterRows()
    {
        await WithObserverAsync(async fixture =>
        {
            using var baseline = fixture.Observe();
            await AssertValidGatherAsync(baseline.RootElement, fixture);
            var actor = fixture.AttachRealActor();
            MintRealAuditRows(actor, 3);
            using var previousPoll = fixture.Observe();
            await Assert.That(previousPoll.RootElement.GetProperty("ok").GetBoolean()).IsTrue();
            var previousRows = previousPoll.RootElement.GetProperty("data").GetProperty("actorAudit");
            var previousTail = previousRows[previousRows.GetArrayLength() - 1].GetProperty("trace_id").GetGuid();
            await Assert.That(previousTail).IsEqualTo(actor.AuditTrace[^1].TraceId);
            MintRealAuditRows(actor, 2);
            var before = actor.AuditTrace.ToArray();

            using var observed = fixture.ObserveWithAuditCursor(previousTail.ToString());
            await AssertProjectedRowsAsync(observed.RootElement, before.SkipWhile(r => r.TraceId != previousTail).ToArray());
            await Assert.That(actor.AuditTrace.Select(r => r.TraceId).SequenceEqual(before.Select(r => r.TraceId))).IsTrue();
            await Assert.That(fixture.Scheduler.IsRunning).IsFalse();
        });
    }

    [Test]
    public async Task PassiveObserve_AuditCursorLatest_ReturnsOneOwnRow()
    {
        await WithObserverAsync(async fixture =>
        {
            using var baseline = fixture.Observe();
            await AssertValidGatherAsync(baseline.RootElement, fixture);
            var actor = fixture.AttachRealActor();
            MintRealAuditRows(actor, 5);
            var before = actor.AuditTrace.ToArray();

            using var observed = fixture.ObserveWithAuditCursor(before[^1].TraceId.ToString());
            await AssertProjectedRowsAsync(observed.RootElement, [before[^1]]);
            await Assert.That(actor.AuditTrace.Select(r => r.TraceId).SequenceEqual(before.Select(r => r.TraceId))).IsTrue();
            await Assert.That(fixture.Scheduler.IsRunning).IsFalse();
        });
    }


    [Test]
    public async Task PassiveObserve_AuditCursorExpired_RejectsWithoutFallback()
    {
        await WithObserverAsync(async fixture =>
        {
            using var baseline = fixture.Observe();
            await AssertValidGatherAsync(baseline.RootElement, fixture);
            var actor = fixture.AttachRealActor();
            MintRealAuditRows(actor, 1);
            var expired = actor.AuditTrace[0].TraceId;
            MintRealAuditRows(actor, 513);
            var before = actor.AuditTrace.ToArray();
            await Assert.That(before.Length).IsEqualTo(512);
            await Assert.That(before.Any(r => r.TraceId == expired)).IsFalse();
            // The real retained tail remains observable; expiry is not a
            // broken fixture, missing actor, or serialization failure.
            using var retained = fixture.Observe();
            await AssertProjectedRowsAsync(retained.RootElement, before);

            using var observed = fixture.ObserveWithAuditCursor(expired.ToString());
            await AssertCursorErrorAsync(observed.RootElement);
            await Assert.That(actor.AuditTrace.Select(r => r.TraceId).SequenceEqual(before.Select(r => r.TraceId))).IsTrue();
            await Assert.That(fixture.Scheduler.IsRunning).IsFalse();
        });
    }

    [Test]
    public async Task PassiveObserve_AuditCursorMalformedOrNull_RejectsWithoutFallback()
    {
        await WithObserverAsync(async fixture =>
        {
            using var baseline = fixture.Observe();
            await AssertValidGatherAsync(baseline.RootElement, fixture);
            var actor = fixture.AttachRealActor();
            MintRealAuditRows(actor, 5);
            var before = actor.AuditTrace.ToArray();
            using var retained = fixture.Observe();
            await AssertProjectedRowsAsync(retained.RootElement, before);

            foreach (var cursor in new object?[] { "not-a-request-guid", null, 42, true })
            {
                using var observed = fixture.ObserveWithAuditCursor(cursor);
                await AssertCursorErrorAsync(observed.RootElement);
                await Assert.That(actor.AuditTrace.Select(r => r.TraceId).SequenceEqual(before.Select(r => r.TraceId))).IsTrue();
            }
            await Assert.That(fixture.Scheduler.IsRunning).IsFalse();
        });
    }

    [Test]
    public async Task PassiveObserve_AliasedGather_LeavesTheQuestUnmappedAndKeepsObserving()
    {
        await WithObserverAsync(async fixture =>
        {
            using var baseline = fixture.Observe();
            await AssertValidGatherAsync(baseline.RootElement, fixture);
            // Quest 266's shape: the gather counts an item alias, which the
            // observer cannot map to one item id.
            fixture.Gather.UseAlias = true;

            using var observed = fixture.Observe();
            await AssertUnmappedAsync(observed.RootElement);
            await Assert.That(observed.RootElement.GetProperty("data").GetProperty("activeQuests").GetArrayLength())
                .IsEqualTo(1);
        });
    }

    private static async Task AssertUnmappedAsync(JsonElement root)
    {
        await Assert.That(root.GetProperty("ok").GetBoolean()).IsTrue();
        var data = root.GetProperty("data");
        await Assert.That(data.GetProperty("objectiveItems").GetArrayLength()).IsEqualTo(0);
        var unmapped = data.GetProperty("objectiveItemsUnmapped");
        await Assert.That(unmapped.GetArrayLength()).IsEqualTo(1);
        await Assert.That(unmapped[0].GetUInt32()).IsEqualTo(QuestId);
    }

    private static void MintRealAuditRows(GameplayActor actor, int count)
    {
        for (var i = 0; i < count; i++)
        {
            if ((i & 1) == 0)
            {
                actor.Observe();
                continue;
            }
            actor.Stop();
        }
    }

    private static async Task AssertProjectedRowsAsync(JsonElement root, IReadOnlyList<ActorAuditRecord> expected)
    {
        await Assert.That(root.GetProperty("ok").GetBoolean()).IsTrue();
        var rows = root.GetProperty("data").GetProperty("actorAudit");
        await Assert.That(rows.GetArrayLength()).IsEqualTo(expected.Count);
        var actualTraceIds = rows.EnumerateArray().Select(r => r.GetProperty("trace_id").GetGuid());
        await Assert.That(actualTraceIds.SequenceEqual(expected.Select(r => r.TraceId))).IsTrue();
    }

    private static async Task AssertCursorErrorAsync(JsonElement root)
    {
        await Assert.That(root.GetProperty("ok").GetBoolean()).IsFalse();
        await Assert.That(root.TryGetProperty("data", out _)).IsFalse();
    }

    private static async Task AssertValidGatherAsync(JsonElement root, ObserverFixture fixture)
    {
        await Assert.That(root.GetProperty("ok").GetBoolean()).IsTrue();
        var data = root.GetProperty("data");
        var mappings = data.GetProperty("objectiveItems");
        await Assert.That(mappings.GetArrayLength()).IsEqualTo(1);
        var mapping = mappings[0];
        await Assert.That(mapping.GetProperty("questId").GetUInt32()).IsEqualTo(QuestId);
        await Assert.That(mapping.GetProperty("actId").GetUInt32()).IsEqualTo(GatherActId);
        await Assert.That(mapping.GetProperty("componentId").GetUInt32()).IsEqualTo(GatherComponentId);
        await Assert.That(mapping.GetProperty("objectiveIndex").GetInt32()).IsEqualTo(0);
        await Assert.That(mapping.GetProperty("itemId").GetUInt32()).IsEqualTo(ItemId);
        await Assert.That(mapping.GetProperty("required").GetInt32()).IsEqualTo(3);
        var active = data.GetProperty("activeQuests");
        await Assert.That(active.GetArrayLength()).IsEqualTo(1);
        await Assert.That(active[0].GetProperty("questId").GetUInt32()).IsEqualTo(QuestId);
        await Assert.That(active[0].GetProperty("step").GetString()).IsEqualTo(nameof(QuestComponentKind.Progress));
        await Assert.That(active[0].GetProperty("objectives")[0].GetInt32()).IsEqualTo(0);
        await Assert.That(Convert.FromBase64String(active[0].GetProperty("data").GetString()!)
            .SequenceEqual(fixture.Quest.WriteData())).IsTrue();
        await Assert.That(data.GetProperty("snapshotStartedAtUtc").GetDateTime() <=
            data.GetProperty("observedAtUtc").GetDateTime()).IsTrue();
        await Assert.That(data.GetProperty("enrollmentRegistered").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("enrollmentSchedulerRunning").GetBoolean()).IsFalse();
        await Assert.That(data.GetProperty("stepsBefore").GetInt64()).IsEqualTo(0L);
        await Assert.That(data.GetProperty("stepsAfter").GetInt64()).IsEqualTo(0L);
        await Assert.That(data.GetProperty("actorAudit").GetArrayLength()).IsEqualTo(0);
        await Assert.That(data.GetProperty("liveAction").ValueKind).IsEqualTo(JsonValueKind.Null);
    }

    private static async Task WithObserverAsync(Func<ObserverFixture, Task> body)
    {
        var previousProvider = SingletonContainer.ServiceProvider;
        var (actor, session) = GameplayActorTestRig.CreateActor("Observe" + Guid.NewGuid().ToString("N"));
        var character = actor.Character;
        character.Slots = new ActionSlot[Character.MaxActionSlots];
        for (var i = 0; i < character.Slots.Length; i++)
            character.Slots[i] = new ActionSlot();
        var sessions = (ConcurrentDictionary<string, HeadlessSession>)typeof(BotDriveBridge)
            .GetField("PersistentBotSessions", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        var key = character.Name.NormalizeName();
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
            .GetField("_worlds", PrivateInstance)!.GetValue(WorldManager.Instance)!;
        var questTemplates = (Dictionary<uint, QuestTemplate>)typeof(QuestManager)
            .GetField("_questTemplates", PrivateInstance)!.GetValue(QuestManager.Instance)!;
        var components = (Dictionary<uint, QuestComponentTemplate>)typeof(QuestManager)
            .GetField("_componentTemplates", PrivateInstance)!.GetValue(QuestManager.Instance)!;
        questTemplates.TryGetValue(QuestId, out var previousQuest);
        components.TryGetValue(GatherComponentId, out var previousGatherComponent);
        components.TryGetValue(OtherComponentId, out var previousOtherComponent);
        var template = new QuestTemplate { Id = QuestId, Level = 2 };
        var component = new QuestComponentTemplate(template)
        {
            Id = GatherComponentId, KindId = QuestComponentKind.Progress,
        };
        var gather = new QuestActObjItemGather(component)
        {
            ActId = GatherActId, DetailId = GatherActId,
            DetailType = nameof(QuestActObjItemGather), Count = 3, ItemId = ItemId,
            ThisComponentObjectiveIndex = 0, ParentQuestTemplate = template,
        };
        component.ActTemplates.Add(gather);
        template.Components.Add(component.Id, component);
        var addedWorld = false;
        var addedSession = false;
        ServiceProvider? provider = null;
        try
        {
            questTemplates[QuestId] = template;
            components[GatherComponentId] = component;
            addedWorld = worlds.TryAdd(session.World.Id, session.World);
            addedSession = sessions.TryAdd(key, session);
            if (!addedSession)
                throw new InvalidOperationException("Observer fixture session identity collision");
            var executor = new BotRoamStepExecutor();
            PlayerBotScheduler scheduler = null!;
            var manager = new PlayerBotManager(new PlayerBotLifecycleAdapter(
                new CharacterLifecycleService(), new Lazy<PlayerBotScheduler>(() => scheduler)));
            scheduler = new PlayerBotScheduler(manager, executor,
                new PlayerBotSchedulerOptions { SubscribeToTickManager = false });
            provider = new ServiceCollection()
                .AddSingleton<IPlayerBotManager>(manager)
                .AddSingleton<IPlayerBotScheduler>(scheduler)
                .AddSingleton(executor).BuildServiceProvider();
            SingletonContainer.ServiceProvider = provider;
            var quest = new Quest(template, character, QuestManager.Instance, TaskManager.Instance,
                SkillManager.Instance, ExpressTextManager.Instance, WorldManager.Instance);
            quest.SkipUpdatePackets();
            quest.Status = QuestStatus.Progress;
            quest.Step = QuestComponentKind.Progress;
            character.Quests.ActiveQuests[QuestId] = quest;
            await body(new ObserverFixture(session, quest, template, gather, components, manager, scheduler, executor));
        }
        finally
        {
            SingletonContainer.ServiceProvider = previousProvider;
            provider?.Dispose();
            character.Quests.ActiveQuests.TryRemove(QuestId, out _);
            if (addedSession && sessions.TryGetValue(key, out var registeredSession) && ReferenceEquals(registeredSession, session))
                sessions.TryRemove(key, out _);
            if (addedWorld && worlds.TryGetValue(session.World.Id, out var registeredWorld) && ReferenceEquals(registeredWorld, session.World))
                worlds.TryRemove(session.World.Id, out _);
            if (ReferenceEquals(WorldManager.Instance.GetCharacterByObjId(character.ObjId), character))
                WorldManager.Instance.TryRemoveCharacter(character.ObjId);
            if (previousQuest == null) questTemplates.Remove(QuestId);
            else questTemplates[QuestId] = previousQuest;
            if (previousGatherComponent == null) components.Remove(GatherComponentId);
            else components[GatherComponentId] = previousGatherComponent;
            if (previousOtherComponent == null) components.Remove(OtherComponentId);
            else components[OtherComponentId] = previousOtherComponent;
        }
    }

    private sealed class ObserverFixture(HeadlessSession session, Quest quest, QuestTemplate template,
        QuestActObjItemGather gather, Dictionary<uint, QuestComponentTemplate> components,
        PlayerBotManager manager, PlayerBotScheduler scheduler, BotRoamStepExecutor executor)
    {
        public HeadlessSession Session { get; } = session;
        public Quest Quest { get; } = quest;
        public QuestActObjItemGather Gather { get; } = gather;
        public QuestActObjMonsterHunt? Hunt { get; private set; }
        public PlayerBotManager Manager { get; } = manager;
        public PlayerBotScheduler Scheduler { get; } = scheduler;

        public GameplayActor AttachRealActor()
            => (GameplayActor)executor.GetOrCreateActor(Session.Character);

        public void AddHunt(QuestComponentKind kind)
        {
            var component = new QuestComponentTemplate(template) { Id = OtherComponentId, KindId = kind };
            Hunt = new QuestActObjMonsterHunt(component)
            {
                ActId = HuntActId, DetailId = HuntActId,
                DetailType = nameof(QuestActObjMonsterHunt), Count = 1, NpcId = 3475,
                ThisComponentObjectiveIndex = 0, ParentQuestTemplate = template,
            };
            component.ActTemplates.Add(Hunt);
            template.Components.Add(component.Id, component);
            components[component.Id] = component;
            if (!Quest.QuestSteps.TryGetValue(kind, out var step))
            {
                step = new QuestStep(kind, Quest);
                Quest.QuestSteps.Add(kind, step);
            }
            var runtime = new QuestComponent(step, component);
            step.Components.Add(component.Id, runtime);
            foreach (var act in runtime.Acts)
                act.Template.InitializeQuest(Quest, act);
        }

        public JsonDocument Observe()
            => InvokeObserveCommand(new { cmd = "quest", bot = Session.Character.Name, op2 = "observe" });

        public JsonDocument ObserveWithAuditCursor(object? cursor)
            => InvokeObserveCommand(new
            {
                cmd = "quest", bot = Session.Character.Name, op2 = "observe", auditAfterTraceId = cursor,
            });

        private static JsonDocument InvokeObserveCommand(object command)
        {
            var handleCommand = typeof(BotDriveBridge).GetMethod("HandleCommand", PrivateInstance)!;
            return JsonDocument.Parse((string)handleCommand.Invoke(BotDriveBridge.Instance,
                [JsonSerializer.Serialize(command)])!);
        }
    }
}
