using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Commons.Network.Core;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.Tasks.Skills;
using AAEmu.UnitTests.Game.Core.Managers.Bots;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// PRISON-01 release-on-expiry rig (justice domain).
///
/// Locks the EXISTING, data-driven release contract: the canonical
/// <c>compact.sqlite3</c> rows for the sentence buffs
/// <c>Prisoner_Nuian (631)</c> / <c>Prisoner_Haranyan (2028)</c> each carry a
/// <c>buff_triggers</c> row with <c>event_id = 6</c>
/// (<see cref="BuffEventTriggerKind.Timeout"/>) pointing at a
/// <c>special_effects</c> row whose <c>special_effect_type_id = 25</c>
/// (<c>SpecialType.Return</c>) and whose <c>value1</c> is the worldgate id —
/// 48 ("Nuian Jail Exit") / 262 ("Haranya Jail Exit") in
/// <c>Data/Portal/worldgates.json</c>.
///
/// So when the sentence buff falls off, the engine's real expiry chain
/// (DispelTask → Buff.ScheduleEffect → Buff.StopEffectTask →
/// BuffTriggersHandler → BuffTrigger.Execute → SpecialEffect.Apply →
/// <c>Return.Execute</c>) already teleports the released prisoner to the
/// canonical jail-exit point. These tests read that from the SHIPPED data and
/// drive it through the REAL engine path — nothing is hardcoded into the
/// production code to make them pass.
///
/// Data: canonical compact.sqlite3 (md5 78b3bdbf038db3b927056106efdf91af) +
/// the repo-shipped Data/Portal/worldgates.json. Both are read-only reference
/// data; the tests soft-skip when the DB is absent (bare worker clone).
/// </summary>
[NotInParallel]
public class PrisonReleaseRigTests
{
    private const BindingFlags StaticFlags = BindingFlags.NonPublic | BindingFlags.Static;
    private const BindingFlags InstanceFlags = BindingFlags.NonPublic | BindingFlags.Instance;

    private const uint PrisonerNuian = (uint)BuffConstants.Prisoner_Nuian;       // 631
    private const uint PrisonerHaranyan = (uint)BuffConstants.Prisoner_Haranyan; // 2028
    private const uint OrdinaryBuff = 15;                                        // canonical trigger-free Good buff
    /// <summary>Canonical 2167 나쁜사람 — kind Bad, save_rule Normal: the ordinary debuff the fix must NOT persist.</summary>
    private const uint OrdinaryBadDebuff = 2167;

    /// <summary>Nuian jail exit worldgate id (canonical special_effects 563 value1).</summary>
    private const uint NuianJailExitGate = 48;
    /// <summary>Haranya jail exit worldgate id (canonical special_effects 3129 value1).</summary>
    private const uint HaranyaJailExitGate = 262;

    /// <summary>SCOffsets.SCTeleportUnitPacket.</summary>
    private const ushort SCTeleportUnit = 0x0072;
    /// <summary>SCOffsets.SCLoadInstancePacket.</summary>
    private const ushort SCLoadInstance = 0x0197;
    /// <summary>TeleportReason.MoveToLocation — what Return.Execute sends.</summary>
    private const byte TeleportReasonMoveToLocation = 14;

    private sealed class CaptureSession : ISession
    {
        public List<byte[]> Frames { get; } = [];

        public System.Net.IPAddress Ip => System.Net.IPAddress.Loopback;
        public uint SessionId => 1;
        public System.Net.Sockets.Socket Socket => null;

        public void SendPacket(byte[] packet) => Frames.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }

        public ushort OpcodeAt(byte[] frame) => (ushort)(frame[6] | (frame[7] << 8));
        public IEnumerable<byte[]> WithOpcode(ushort opcode) => Frames.Where(f => f.Length > 8 && OpcodeAt(f) == opcode);
    }

    private static void SetSingleton<T>(object value) where T : class =>
        typeof(Singleton<T>).GetField("s_instance", StaticFlags)!.SetValue(null, value);

    private static void SetField(object target, string name, object value) =>
        target.GetType().GetField(name, InstanceFlags)!.SetValue(target, value);

    private static string DbPath => Path.Combine(AAEmu.Commons.IO.FileManager.AppPath, "Data", "compact.sqlite3");

    /// <summary>
    /// Installs the production data surface (real SkillManager from canonical data,
    /// real PortalManager from the shipped Data/Portal JSON) and pins
    /// main_world to instance id 0 exactly as WorldManager.Load does
    /// (CreateWorldInstance(..., fixedInstanceId: 0)), because Return.Execute
    /// builds a fresh instanceId-0 Transform.
    /// </summary>
    private sealed class DataScope : IDisposable
    {
        private readonly object _prevSkill = SkillManager.PeekInstance;
        private readonly object _prevPortal = PortalManager.PeekInstance;
        private readonly object _prevWorld = WorldManager.PeekInstance;
        private readonly object _prevZone = ZoneManager.PeekInstance;
        private readonly object _prevEffectTask = EffectTaskManager.PeekInstance;
        private WorldInstance _registeredWorld;
        private WorldInstance _priorMainWorld;

        public bool DataPresent { get; }
        public SkillManager Skills { get; }
        public PortalManager Portals { get; }

        private static ConcurrentDictionary<uint, WorldInstance> WorldRegistry =>
            WorldManager.PeekInstance == null
                ? null
                : (ConcurrentDictionary<uint, WorldInstance>)typeof(WorldManager)
                    .GetField("_worlds", InstanceFlags)!.GetValue(WorldManager.Instance);

        public DataScope()
        {
            DataPresent = File.Exists(DbPath);
            if (!DataPresent)
                return;

            try
            {
                // The shared rig is missing-only (s_seeded is one-shot), but its
                // Seed* legs dereference SkillManager.Instance and CreateActor
                // dereferences WorldManager.Instance. Sibling swap rigs restore
                // those slots unconditionally, so re-establish the two DI-only
                // singletons when they are absent (the rig's own
                // SeedBaseSurface shape) BEFORE anything touches them.
                if (SkillManager.PeekInstance == null)
                    SetSingleton<SkillManager>(new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object));
                if (WorldManager.PeekInstance == null)
                {
                    SetSingleton<WorldManager>(new WorldManager(
                        Mock.Of<ITickManager>().Object,
                        Mock.Of<IWorldIdManager>().Object,
                        new Lazy<IZoneManager>(() => Mock.Of<IZoneManager>().Object),
                        new Lazy<IIndunManager>(() => Mock.Of<IIndunManager>().Object),
                        new Lazy<IFamilyManager>(() => Mock.Of<IFamilyManager>().Object)));
                }

                // Production data surface the release chain reads.
                Skills = new SkillManager(Mock.Of<IAnimationManager>().Object, Mock.Of<IPlotManager>().Object);
                SetSingleton<SkillManager>(Skills);
                Skills.Load();

                if (EffectTaskManager.PeekInstance == null)
                    SetSingleton<EffectTaskManager>(new EffectTaskManager(Mock.Of<ITaskManager>().Object));

                Portals = new PortalManager(
                    Mock.Of<ILocalizationManager>().Object,
                    Mock.Of<IWorldManager>().Object,
                    Mock.Of<IZoneManager>().Object,
                    Mock.Of<INpcManager>().Object,
                    Mock.Of<IObjectIdManager>().Object,
                    Mock.Of<ITaskManager>().Object);
                Portals.Load();
                SetSingleton<PortalManager>(Portals);

                // ZoneManager is DI-only; the release chain touches it solely for
                // the zone/chat fanout of the fresh Transform's ZoneId assignment.
                if (ZoneManager.PeekInstance == null)
                {
                    var zones = new ZoneManager(Mock.Of<IWorldManager>().Object);
                    SetField(zones, "_zones", new Dictionary<uint, Zone>());
                    SetField(zones, "_zoneIdToKey", new Dictionary<uint, uint>());
                    SetField(zones, "_groups", new Dictionary<uint, ZoneGroup>());
                    SetSingleton<ZoneManager>(zones);
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            typeof(Singleton<SkillManager>).GetField("s_instance", StaticFlags)!.SetValue(null, _prevSkill);
            typeof(Singleton<PortalManager>).GetField("s_instance", StaticFlags)!.SetValue(null, _prevPortal);
            typeof(Singleton<WorldManager>).GetField("s_instance", StaticFlags)!.SetValue(null, _prevWorld);
            typeof(Singleton<ZoneManager>).GetField("s_instance", StaticFlags)!.SetValue(null, _prevZone);
            typeof(Singleton<EffectTaskManager>).GetField("s_instance", StaticFlags)!.SetValue(null, _prevEffectTask);

            // The shared WorldManager registry is process-wide: remove exactly the
            // entries we added (never other suites' worlds), restoring any prior
            // main_world entry at key 0.
            if (_registeredWorld != null)
            {
                var worlds = WorldRegistry;
                if (worlds != null)
                {
                    worlds.TryRemove(_registeredWorld.Id, out _);
                    if (_priorMainWorld != null)
                        worlds[0] = _priorMainWorld;
                    else
                        worlds.TryRemove(0, out _);
                }
            }
        }

        /// <summary>
        /// Builds a fresh headless character on this scope's data surface,
        /// registering its world as main_world so a release can teleport.
        /// </summary>
        public (GameplayActor Actor, CaptureSession Capture) NewPrisoner(string name)
        {
            var (actor, session) = GameplayActorTestRig.CreateActor(name);
            RegisterWorld(session);

            var capture = new CaptureSession();
            actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
            return (actor, capture);
        }

        /// <summary>Adds a canonical buff for a fixed duration.</summary>
        public Buff AddBuff(Character character, uint buffId, int durationMs)
        {
            var template = Skills.GetBuffTemplate(buffId)
                ?? throw new InvalidOperationException($"canonical buff {buffId} not found");
            var buff = new Buff(character, character, new SkillCasterUnit(character.ObjId), template, null, DateTime.UtcNow);
            character.Buffs.AddBuff(buff, forcedDuration: durationMs);
            return buff;
        }

        /// <summary>
        /// Rebuilds a buff the way <c>Buffs.LoadActiveBuffs</c> does on login:
        /// Duration = the saved remaining time, applied via
        /// AddBuff(forcedDuration: remainingMs). This is the production resume
        /// shape — the release trigger is re-subscribed by Buffs.AddBuff.
        /// </summary>
        public Buff RestoreFromSavedRow(Character character, uint buffId, int remainingMs)
        {
            var template = Skills.GetBuffTemplate(buffId)
                ?? throw new InvalidOperationException($"canonical buff {buffId} not found");

            var buff = new Buff(character, character, new SkillCasterUnit(character.ObjId),
                template, null, DateTime.UtcNow)
            {
                Duration = remainingMs
            };

            character.Buffs.AddBuff(buff, forcedDuration: remainingMs);
            return buff;
        }

        /// <summary>
        /// Applies a canonical buff to a fresh headless character, expires it
        /// through the REAL DispelTask the engine schedules, and returns the
        /// captured outbound frames plus the character.
        /// </summary>
        public (GameplayActor Actor, CaptureSession Capture) ApplyAndExpire(uint buffId)
        {
            var (actor, session) = GameplayActorTestRig.CreateActor($"prison-{buffId}");
            var character = session.Character;

            RegisterWorld(session);

            var capture = new CaptureSession();
            character.Connection = new GameConnection(capture) { ActiveChar = character };

            var template = Skills.GetBuffTemplate(buffId);
            if (template == null)
                throw new InvalidOperationException($"canonical buff {buffId} not found");

            var buff = new Buff(character, character, new SkillCasterUnit(character.ObjId), template, null, DateTime.UtcNow);
            character.Buffs.AddBuff(buff, forcedDuration: 1000);

            // Expire deterministically, then run the production expiry task.
            buff.StartTime = DateTime.UtcNow.AddSeconds(-10);
            new DispelTask(buff).Execute();

            return (actor, capture);
        }

        /// <summary>
        /// Production pins main_world to instance id 0
        /// (WorldManager.Load -> CreateWorldInstance(..., fixedInstanceId: 0))
        /// and Return.Execute builds a fresh instanceId-0 Transform, so the
        /// registry must resolve 0 or the release NREs on ParentWorld.
        /// </summary>
        private void RegisterWorld(HeadlessSession session)
        {
            var worlds = WorldRegistry;
            _priorMainWorld = worlds.TryGetValue(0, out var prior) ? prior : null;
            worlds[0] = session.World;
            worlds[session.World.Id] = session.World;
            _registeredWorld = session.World;
        }
    }

    /// <summary>
    /// (c) + (a) The prison sentence is persisted on logout while an ordinary Bad
    /// debuff is NOT — the fix must not leak generality.
    ///
    /// This is the exact predicate <c>SaveActiveBuffs</c> gates every row on
    /// (<c>if (!ShouldPersistBuff(buff)) continue;</c>), so asserting it pins what
    /// actually reaches <c>character_active_buffs</c>.
    /// </summary>
    [Test]
    public async Task SentenceIsPersisted_OrdinaryBadDebuffIsNot()
    {
        using var scope = new DataScope();
        if (!scope.DataPresent)
        {
            Console.WriteLine($"[PrisonRelease] SKIPPED — {DbPath} not found");
            return;
        }

        var (actor, _) = scope.NewPrisoner("prison-persist");
        var character = actor.Character;

        // Canonical sentence buffs are kind_id 2 (Bad) with save_rule_id 1 (Normal)
        // — exactly the pair the "don't save debuffs" rule used to reject.
        var sentence = scope.AddBuff(character, PrisonerNuian, durationMs: 1_800_000);
        var ordinaryBad = scope.AddBuff(character, OrdinaryBadDebuff, durationMs: 30_000);

        await Assert.That(sentence.Template.Kind).IsEqualTo(BuffKind.Bad);
        await Assert.That(sentence.Template.SaveRuleId).IsEqualTo(BuffSaveRuleType.Normal);
        await Assert.That(ordinaryBad.Template.Kind).IsEqualTo(BuffKind.Bad);

        await Assert.That(Buffs.ShouldPersistBuff(sentence)).IsTrue();
        await Assert.That(Buffs.ShouldPersistBuff(ordinaryBad)).IsFalse();

        // An ordinary GOOD buff under the duration floor still does not persist
        // (the pre-existing rule is untouched).
        var shortGood = scope.AddBuff(character, OrdinaryBuff, durationMs: 5_000);
        await Assert.That(Buffs.ShouldPersistBuff(shortGood)).IsFalse();

        // Nothing else about the fix generalizes: a finished/expired sentence is
        // still refused.
        sentence.State = EffectState.Finished;
        await Assert.That(Buffs.ShouldPersistBuff(sentence)).IsFalse();
    }

    /// <summary>
    /// (a) A sentence saved mid-way resumes with the REMAINING time on login —
    /// not restarted at full length, not lost.
    ///
    /// Drives the production save/restore shape: the loader rebuilds the buff with
    /// <c>Duration = remainingMs</c> and <c>AddBuff(forcedDuration: remainingMs)</c>
    /// (Buffs.LoadActiveBuffs). The saved remaining time comes from the same
    /// <see cref="Buffs.ComputeRemainingMs"/> the loader uses.
    /// </summary>
    [Test]
    public async Task ResumedSentenceKeepsRemainingTime_NotFullDuration()
    {
        using var scope = new DataScope();
        if (!scope.DataPresent)
        {
            Console.WriteLine($"[PrisonRelease] SKIPPED — {DbPath} not found");
            return;
        }

        var (actor, _) = scope.NewPrisoner("prison-resume");
        var character = actor.Character;

        // Sentence issued 18 minutes ago with a 30-minute term: 12 minutes remain.
        var sentence = scope.AddBuff(character, PrisonerNuian, durationMs: 1_800_000);
        sentence.StartTime = DateTime.UtcNow.AddMinutes(-18);

        var timeLeftAtSave = (int)sentence.GetTimeLeft();
        await Assert.That(timeLeftAtSave).IsGreaterThan(11 * 60_000);
        await Assert.That(timeLeftAtSave).IsLessThan(13 * 60_000);
        await Assert.That(Buffs.ShouldPersistBuff(sentence)).IsTrue();

        // Game-time sentence (real_time = false) → the timer was paused offline,
        // so the saved remaining time is resumed verbatim.
        var resumedMs = Buffs.ComputeRemainingMs(timeLeftAtSave, realTime: false,
            savedAt: DateTime.UtcNow.AddHours(-5), nowUtc: DateTime.UtcNow);
        await Assert.That(resumedMs).IsEqualTo(timeLeftAtSave);

        // Rebuild exactly as LoadActiveBuffs does on login.
        var restored = scope.RestoreFromSavedRow(character, PrisonerNuian, resumedMs);

        await Assert.That(restored).IsNotNull();
        await Assert.That(character.Buffs.CheckBuff(PrisonerNuian)).IsTrue();
        await Assert.That(character.Buffs.CheckBuffTag((uint)BuffConstants.TagPrisoner)).IsTrue();

        // Resumed at ~12 minutes, NOT restarted at the full 30.
        var remaining = restored!.GetTimeLeft();
        await Assert.That(remaining).IsGreaterThan(11 * 60_000);
        await Assert.That(remaining).IsLessThan(13 * 60_000);
        await Assert.That(restored.Duration).IsLessThan(1_800_000);

        // A real-time buff is different by design: offline time is subtracted.
        await Assert.That(Buffs.ComputeRemainingMs(60_000, realTime: true,
            savedAt: DateTime.UtcNow.AddMinutes(-5), nowUtc: DateTime.UtcNow)).IsEqualTo(60_000 - 300_000);
    }

    /// <summary>
    /// (b) The resumed sentence still releases through the SAME data-driven path
    /// when it expires — no separate release authority for the resume case.
    /// </summary>
    [Test]
    public async Task ResumedSentenceStillReleasesToCanonicalJailExit()
    {
        using var scope = new DataScope();
        if (!scope.DataPresent)
        {
            Console.WriteLine($"[PrisonRelease] SKIPPED — {DbPath} not found");
            return;
        }

        foreach (var (buffId, gateId) in new[] { (PrisonerNuian, NuianJailExitGate), (PrisonerHaranyan, HaranyaJailExitGate) })
        {
            var gate = scope.Portals.GetWorldGatesById(gateId)!;
            var (actor, capture) = scope.NewPrisoner($"prison-resume-{buffId}");
            var character = actor.Character;

            // 12 minutes left of a 30-minute sentence.
            const int remainingMs = 12 * 60_000;
            var restored = scope.RestoreFromSavedRow(character, buffId, remainingMs);
            await Assert.That(restored).IsNotNull();

            // Resume must re-subscribe the canonical Timeout release trigger.
            await Assert.That(scope.Skills.GetBuffTriggerTemplates(buffId)).HasCount().EqualTo(1);

            // Let the resumed sentence run out through the production task.
            restored!.StartTime = DateTime.UtcNow.AddMilliseconds(-remainingMs - 1000);
            new DispelTask(restored).Execute();

            await Assert.That(character.Buffs.CheckBuff(buffId)).IsFalse();
            await Assert.That(character.Buffs.CheckBuffTag((uint)BuffConstants.TagPrisoner)).IsFalse();

            var pos = character.Transform.World.Position;
            await Assert.That(pos.X).IsEqualTo(gate.X);
            await Assert.That(pos.Y).IsEqualTo(gate.Y);
            await Assert.That(pos.Z).IsEqualTo(gate.Z);

            var teleports = capture.WithOpcode(SCTeleportUnit).ToList();
            await Assert.That(teleports).HasCount().EqualTo(1);
            await Assert.That(teleports[0][8]).IsEqualTo(TeleportReasonMoveToLocation);
        }
    }

    /// <summary>
    /// The canonical sentence buffs carry the release trigger. This is the
    /// data fact the whole contract rests on — if a future data refresh drops
    /// it, this fails loudly instead of the release silently disappearing.
    /// </summary>
    [Test]
    public async Task SentenceBuffs_CarryCanonicalTimeoutReturnTrigger()
    {
        using var scope = new DataScope();
        if (!scope.DataPresent)
        {
            Console.WriteLine($"[PrisonRelease] SKIPPED — {DbPath} not found");
            return;
        }

        var expectedGate = new Dictionary<uint, uint>
        {
            [PrisonerNuian] = NuianJailExitGate,
            [PrisonerHaranyan] = HaranyaJailExitGate
        };

        foreach (var (buffId, gateId) in expectedGate)
        {
            var triggers = scope.Skills.GetBuffTriggerTemplates(buffId);

            await Assert.That(triggers).HasCount().EqualTo(1);
            await Assert.That(triggers[0].Kind).IsEqualTo(BuffEventTriggerKind.Timeout);

            await Assert.That(triggers[0].Effect).IsNotNull();
            var special = (SpecialEffect)triggers[0].Effect;
            await Assert.That(special.SpecialEffectTypeId).IsEqualTo(SpecialType.Return);
            await Assert.That((uint)special.Value1).IsEqualTo(gateId);

            // The named target must actually resolve in the shipped portal data.
            var gate = scope.Portals.GetWorldGatesById(gateId);
            await Assert.That(gate).IsNotNull();
            await Assert.That(gate!.ZoneId > 0u).IsTrue();
        }
    }

    /// <summary>
    /// (a) The release fires when the sentence buff expires: the Prisoner buff
    /// is gone, the character is moved to the canonical jail-exit worldgate
    /// position, and the client is told (teleport + load-instance frames).
    /// </summary>
    [Test]
    public async Task SentenceBuffExpiry_MovesPrisonerToCanonicalJailExit()
    {
        using var scope = new DataScope();
        if (!scope.DataPresent)
        {
            Console.WriteLine($"[PrisonRelease] SKIPPED — {DbPath} not found");
            return;
        }

        foreach (var (buffId, gateId) in new[] { (PrisonerNuian, NuianJailExitGate), (PrisonerHaranyan, HaranyaJailExitGate) })
        {
            var gate = scope.Portals.GetWorldGatesById(gateId)!;
            var (actor, capture) = scope.ApplyAndExpire(buffId);
            var character = actor.Character;

            // Prisoner state cleared — no lingering prisoner tag.
            await Assert.That(character.Buffs.CheckBuff(buffId)).IsFalse();
            await Assert.That(character.Buffs.CheckBuffTag((uint)BuffConstants.TagPrisoner)).IsFalse();

            // Released to the canonical exit, not left standing in the cell.
            var pos = character.Transform.World.Position;
            await Assert.That(pos.X).IsEqualTo(gate.X);
            await Assert.That(pos.Y).IsEqualTo(gate.Y);
            await Assert.That(pos.Z).IsEqualTo(gate.Z);

            // The client is actually told: teleport frame with the exit coords.
            var teleports = capture.WithOpcode(SCTeleportUnit).ToList();
            await Assert.That(teleports).HasCount().EqualTo(1);
            await Assert.That(teleports[0][8]).IsEqualTo(TeleportReasonMoveToLocation);
            await Assert.That(capture.WithOpcode(SCLoadInstance).Any()).IsTrue();
        }
    }

    /// <summary>
    /// (b) An ordinary non-prisoner buff expiry is unaffected: it emits no
    /// teleport and leaves the character exactly where it was.
    /// </summary>
    [Test]
    public async Task OrdinaryBuffExpiry_EmitsNoTeleportAndLeavesPosition()
    {
        using var scope = new DataScope();
        if (!scope.DataPresent)
        {
            Console.WriteLine($"[PrisonRelease] SKIPPED — {DbPath} not found");
            return;
        }

        // Canonical buff 15 is a plain Good buff with no timeout trigger.
        await Assert.That(scope.Skills.GetBuffTriggerTemplates(OrdinaryBuff)).IsEmpty();

        var (actor, capture) = scope.ApplyAndExpire(OrdinaryBuff);
        var character = actor.Character;
        var pos = character.Transform.World.Position;

        await Assert.That(character.Buffs.CheckBuff(OrdinaryBuff)).IsFalse();
        await Assert.That(capture.WithOpcode(SCTeleportUnit)).IsEmpty();
        await Assert.That(capture.WithOpcode(SCLoadInstance)).IsEmpty();
        await Assert.That(pos.X).IsEqualTo(0f);
        await Assert.That(pos.Y).IsEqualTo(0f);
        await Assert.That(pos.Z).IsEqualTo(0f);
    }
}
