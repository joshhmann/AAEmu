using System.Reflection;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Registry rig for IPlayerBotManager (slice #5): lifecycle round-trips
/// through the manager, registry semantics, runtime ownership, diagnostics
/// counters, concurrency, and isolation from AIManager (bots never enter the
/// NPC AI ticker — review deliverable 1-C).
/// </summary>
public class PlayerBotManagerTests
{
    /// <summary>Deterministic lifecycle seam: records every call, configurable results.</summary>
    private sealed class RecordingLifecycle : IPlayerBotLifecycleService
    {
        // Normal leave now revalidates the real world's Character identity.
        public RecordingLifecycle() => GameplayActorTestRig.Seed();

        public bool ActivationResult { get; set; } = true;
        public bool DeactivationResult { get; set; } = true;
        public bool ThrowOnActivate { get; set; }
        public bool ThrowOnDeactivate { get; set; }
        public Action<Character, string>? OnDeactivate { get; set; }
        public bool RetirementResult { get; set; } = true;
        public bool ThrowOnRetire { get; set; }
        public Func<PlayerBotRuntime, ulong, string, bool>? OnRetire { get; set; }
        public List<(PlayerBotRuntime Owner, ulong Generation, string Reason)> RetirementCalls { get; } = [];

        public bool TryRetireNativeWork(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason)
        {
            lock (RetirementCalls)
                RetirementCalls.Add((expectedOwner, expectedGeneration, reason));
            if (ThrowOnRetire)
                throw new InvalidOperationException("simulated native retirement failure");
            return OnRetire?.Invoke(expectedOwner, expectedGeneration, reason) ?? RetirementResult;
        }

        public List<(Character Character, object? Context)> ActivateCalls { get; } = [];
        public List<(Character Character, string Reason)> DeactivateCalls { get; } = [];

        public bool ActivateHeadless(Character character, object? botContext)
        {
            if (ThrowOnActivate)
                throw new InvalidOperationException("simulated lifecycle failure");
            lock (ActivateCalls)
                ActivateCalls.Add((character, botContext));
            return ActivationResult;
        }

        public bool Deactivate(Character character, string reason)
        {
            if (ThrowOnDeactivate)
                throw new InvalidOperationException("simulated teardown failure");
            lock (DeactivateCalls)
                DeactivateCalls.Add((character, reason));
            OnDeactivate?.Invoke(character, reason);
            return DeactivationResult;
        }
    }

    private static Character CreateCharacter(uint id, string name = "bot")
        => new(new UnitCustomModelParams()) { Id = id, Name = name };

    #region Spawn / registry

    [Test]
    public async Task Spawn_NewCharacter_RegistersAndRecordsOwnership()
    {
        var manager = new PlayerBotManager(new RecordingLifecycle());
        var character = CreateCharacter(1, "citizen-1");

        var result = manager.Spawn(character, "population-director");

        await Assert.That(result).IsTrue();
        await Assert.That(manager.Count).IsEqualTo(1);
        await Assert.That(manager.ActiveCount).IsEqualTo(0);

        var found = manager.TryGet(1, out var runtime);
        await Assert.That(found).IsTrue();
        await Assert.That(runtime).IsNotNull();
        await Assert.That(runtime!.CharacterId).IsEqualTo(1u);
        await Assert.That(runtime.Character).IsEqualTo(character);
        await Assert.That(runtime.State).IsEqualTo(PlayerBotState.Registered);
        await Assert.That(runtime.Owner).IsEqualTo("population-director");
    }

    [Test]
    public async Task Spawn_DuplicateCharacterId_ReturnsFalse_AndCountsFailedSpawn()
    {
        var manager = new PlayerBotManager(new RecordingLifecycle());
        manager.Spawn(CreateCharacter(1, "first"), "owner-a");

        var result = manager.Spawn(CreateCharacter(1, "second"), "owner-b");

        await Assert.That(result).IsFalse();
        await Assert.That(manager.Count).IsEqualTo(1);
        await Assert.That(manager.GetDiagnostics().FailedSpawns).IsEqualTo(1);
        await Assert.That(manager.GetDiagnostics().TotalSpawns).IsEqualTo(1);
    }

    [Test]
    public async Task Spawn_NullCharacter_Throws()
    {
        var manager = new PlayerBotManager(new RecordingLifecycle());

        await Assert.That(() => manager.Spawn(null!, "owner")).Throws<ArgumentNullException>();
    }

    [Test]
    public async Task TryGet_UnknownCharacterId_ReturnsFalse()
    {
        var manager = new PlayerBotManager(new RecordingLifecycle());

        var found = manager.TryGet(999, out var runtime);

        await Assert.That(found).IsFalse();
        await Assert.That(runtime).IsNull();
    }

    #endregion

    #region Activate

    [Test]
    public async Task Activate_RegisteredBot_DelegatesToLifecycleService_WithCharacterAndContext()
    {
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);
        var character = CreateCharacter(7, "field-hand");
        manager.Spawn(character, "owner-a");

        var result = manager.Activate(7, new { crop = "potato" }, "owner-a");

        await Assert.That(result).IsTrue();
        await Assert.That(seam.ActivateCalls).HasCount(1);
        await Assert.That(seam.ActivateCalls[0].Character).IsEqualTo(character);
        await Assert.That(seam.ActivateCalls[0].Context).IsNotNull();

        var runtime = manager.TryGet(7, out var r) ? r : null;
        await Assert.That(runtime!.State).IsEqualTo(PlayerBotState.Active);
        await Assert.That(runtime.Owner).IsEqualTo("owner-a");
        await Assert.That(runtime.ActivatedAtUtc).IsNotNull();
        await Assert.That(manager.ActiveCount).IsEqualTo(1);
        await Assert.That(manager.GetActive()).HasCount(1);
    }

    [Test]
    public async Task Activate_SeamRefuses_KeepsStateRegistered_AndCountsFailure()
    {
        var seam = new RecordingLifecycle { ActivationResult = false };
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(3), "owner-a");

        var result = manager.Activate(3, null, "owner-a");

        await Assert.That(result).IsFalse();
        var found = manager.TryGet(3, out var runtime);
        await Assert.That(found).IsTrue();
        await Assert.That(runtime!.State).IsEqualTo(PlayerBotState.Registered);
        await Assert.That(manager.GetDiagnostics().FailedActivations).IsEqualTo(1);
        await Assert.That(manager.GetDiagnostics().TotalActivations).IsEqualTo(0);
    }

    [Test]
    public async Task Activate_SeamThrows_KeepsStateRegistered_AndCountsFailure()
    {
        var seam = new RecordingLifecycle { ThrowOnActivate = true };
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(4), "owner-a");

        var result = manager.Activate(4, null, "owner-a");

        await Assert.That(result).IsFalse();
        var found = manager.TryGet(4, out var runtime);
        await Assert.That(found).IsTrue();
        await Assert.That(runtime!.State).IsEqualTo(PlayerBotState.Registered);
        await Assert.That(manager.GetDiagnostics().FailedActivations).IsEqualTo(1);
    }

    [Test]
    public async Task Activate_UnknownCharacterId_ReturnsFalse()
    {
        var manager = new PlayerBotManager(new RecordingLifecycle());

        var result = manager.Activate(42, null, "owner-a");

        await Assert.That(result).IsFalse();
        await Assert.That(manager.GetDiagnostics().FailedActivations).IsEqualTo(1);
    }

    [Test]
    public async Task Activate_AlreadyActive_IsRejected()
    {
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(5), "owner-a");
        manager.Activate(5, null, "owner-a");

        var second = manager.Activate(5, null, "owner-b");

        await Assert.That(second).IsFalse();
        await Assert.That(seam.ActivateCalls).HasCount(1);
        await Assert.That(manager.GetDiagnostics().FailedActivations).IsEqualTo(1);
        await Assert.That(manager.ActiveCount).IsEqualTo(1);
    }

    #endregion

    #region Deactivate

    [Test]
    public async Task Deactivate_ActiveBot_DelegatesToLifecycleService_WithReason()
    {
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(8, "tired-citizen"), "owner-a");
        manager.Activate(8, null, "owner-a");

        var result = manager.Deactivate(8, "fidelity-downgrade");

        await Assert.That(result).IsTrue();
        await Assert.That(seam.DeactivateCalls).HasCount(1);
        await Assert.That(seam.DeactivateCalls[0].Reason).IsEqualTo("fidelity-downgrade");

        var runtime = manager.TryGet(8, out var r) ? r : null;
        await Assert.That(runtime!.State).IsEqualTo(PlayerBotState.Deactivated);
        await Assert.That(runtime.Owner).IsEqualTo(string.Empty);
        await Assert.That(runtime.DeactivatedAtUtc).IsNotNull();
        await Assert.That(runtime.LastDeactivateReason).IsEqualTo("fidelity-downgrade");
        await Assert.That(manager.ActiveCount).IsEqualTo(0);
        await Assert.That(manager.GetActive()).IsEmpty();
    }

    [Test]
    public async Task Deactivate_NotActiveBot_ReturnsFalse()
    {
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(9), "owner-a");

        // Registered, never activated.
        var result = manager.Deactivate(9, "shutdown");

        await Assert.That(result).IsFalse();
        await Assert.That(seam.DeactivateCalls).IsEmpty();
        await Assert.That(manager.GetDiagnostics().FailedDeactivations).IsEqualTo(1);

        // Unknown id.
        var unknown = manager.Deactivate(999, "shutdown");
        await Assert.That(unknown).IsFalse();
    }

    [Test]
    public async Task Deactivate_SeamRefuses_RetainsOwnerButClosesAdmission_AndCountsFailure()
    {
        var seam = new RecordingLifecycle { DeactivationResult = false };
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(10), "owner-a");
        manager.Activate(10, null, "owner-a");

        var result = manager.Deactivate(10, "shutdown");

        await Assert.That(result).IsFalse();
        var found = manager.TryGet(10, out var runtime);
        await Assert.That(found).IsTrue();
        await Assert.That(runtime!.State).IsEqualTo(PlayerBotState.DeactivationFailed);
        await Assert.That(runtime.Owner).IsEqualTo("owner-a");
        await Assert.That(runtime.DeactivatedAtUtc).IsNull();
        await Assert.That(manager.GetActive()).IsEmpty();
        await Assert.That(manager.Activate(10, null, "resume")).IsFalse();
        await Assert.That(manager.Remove(10)).IsFalse();
        await Assert.That(manager.GetDiagnostics().FailedDeactivations).IsEqualTo(1);
        await Assert.That(manager.GetDiagnostics().TotalDeactivations).IsEqualTo(0);
    }

    [Test]
    public async Task Deactivate_RetiresExactGenerationOutsideSyncBeforePhysicalSave()
    {
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(76), "owner");
        manager.Activate(76, null, "owner");
        manager.TryGet(76, out var runtime);
        var generation = runtime!.Generation;
        var syncWasAvailable = false;
        var admissionClosed = false;
        var savedAfterRetirement = false;
        var acknowledged = false;
        seam.OnRetire = (owner, _, _) =>
        {
            // A boundary retirement must be able to acquire this exact lock.
            syncWasAvailable = Task.Run(() =>
            {
                if (!Monitor.TryEnter(owner.Sync, TimeSpan.FromSeconds(2)))
                    return false;
                try { return owner.State == PlayerBotState.Deactivating; }
                finally { Monitor.Exit(owner.Sync); }
            }).GetAwaiter().GetResult();
            admissionClosed = !manager.GetActive().Any()
                && !manager.Activate(76, null, "must-not-resume") && !manager.Remove(76);
            acknowledged = true;
            return true;
        };
        seam.OnDeactivate = (_, _) => savedAfterRetirement = acknowledged;

        await Assert.That(manager.Deactivate(76, "retire-before-save")).IsTrue();

        await Assert.That(syncWasAvailable).IsTrue();
        await Assert.That(admissionClosed).IsTrue();
        await Assert.That(savedAfterRetirement).IsTrue();
        await Assert.That(seam.RetirementCalls).HasCount(1);
        await Assert.That(seam.RetirementCalls[0].Owner).IsSameReferenceAs(runtime);
        await Assert.That(seam.RetirementCalls[0].Generation).IsEqualTo(generation);
        await Assert.That(seam.RetirementCalls[0].Reason).IsEqualTo("retire-before-save");
        await Assert.That(seam.DeactivateCalls).HasCount(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Deactivate_RefusedOrThrowingRetirementNeverSavesOrReopensAdmission(bool throws)
    {
        var seam = new RecordingLifecycle { RetirementResult = false, ThrowOnRetire = throws };
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(77), "owner");
        manager.Activate(77, null, "owner");
        manager.TryGet(77, out var expected);

        await Assert.That(manager.Deactivate(77, "native-failed")).IsFalse();

        await Assert.That(seam.RetirementCalls).HasCount(1);
        await Assert.That(seam.DeactivateCalls).IsEmpty();
        await Assert.That(manager.TryGet(77, out var retained)).IsTrue();
        await Assert.That(retained).IsSameReferenceAs(expected);
        await Assert.That(retained!.State).IsEqualTo(PlayerBotState.DeactivationFailed);
        await Assert.That(retained.DeactivatedAtUtc).IsNull();
        await Assert.That(manager.Activate(77, null, "retry")).IsFalse();
        await Assert.That(manager.Remove(77)).IsFalse();
        await Assert.That(manager.GetDiagnostics().TotalDeactivations).IsEqualTo(0);
    }

    [Test]
    public async Task Deactivate_GenerationChangedAfterRetirementAcknowledgementNeverSaves()
    {
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(78), "owner");
        manager.Activate(78, null, "owner");
        manager.TryGet(78, out var expected);
        seam.OnRetire = (owner, _, _) =>
        {
            // Controlled stale-ack schedule at the lifecycle seam, not Actor state injection.
            lock (owner.Sync)
                owner.Generation++;
            return true;
        };

        await Assert.That(manager.Deactivate(78, "stale-ack")).IsFalse();

        await Assert.That(seam.DeactivateCalls).IsEmpty();
        await Assert.That(expected!.State).IsEqualTo(PlayerBotState.DeactivationFailed);
        await Assert.That(manager.GetActive()).IsEmpty();
        await Assert.That(manager.Activate(78, null, "retry")).IsFalse();
        await Assert.That(manager.Remove(78)).IsFalse();
    }

    [Test]
    public async Task Deactivate_ClosesAdmissionBeforePhysicalSaveCallback()
    {
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(71), "owner");
        manager.Activate(71, null, "owner");
        var admittedDuringSave = true;
        var removableDuringSave = true;
        var totalDuringSave = -1;
        var countDuringSave = -1;
        var deactivatingDuringSave = -1;
        var failedDuringSave = -1;
        seam.OnDeactivate = (character, _) =>
        {
            admittedDuringSave = manager.GetActive().Any(x => ReferenceEquals(x.Character, character));
            removableDuringSave = manager.Remove(character.Id);
            var diagnostics = manager.GetDiagnostics();
            totalDuringSave = diagnostics.Total;
            deactivatingDuringSave = diagnostics.Deactivating;
            failedDuringSave = diagnostics.DeactivationFailed;
            countDuringSave = manager.Count;
        };

        await Assert.That(manager.Deactivate(71, "save-boundary")).IsTrue();

        await Assert.That(admittedDuringSave).IsFalse();
        await Assert.That(removableDuringSave).IsFalse();
        await Assert.That(manager.ActiveCount).IsEqualTo(0);
        await Assert.That(totalDuringSave).IsEqualTo(countDuringSave);
        await Assert.That(deactivatingDuringSave).IsEqualTo(1);
        await Assert.That(failedDuringSave).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Deactivate_FailedPhysicalLeaveCannotResumeProducer(bool throws)
    {
        var seam = new RecordingLifecycle { DeactivationResult = false, ThrowOnDeactivate = throws };
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(72), "owner");
        manager.Activate(72, null, "owner");

        await Assert.That(manager.Deactivate(72, "failed-save")).IsFalse();

        await Assert.That(manager.GetActive()).IsEmpty();
        await Assert.That(manager.ActiveCount).IsEqualTo(0);
        await Assert.That(manager.TryGet(72, out var retained)).IsTrue();
        await Assert.That(retained!.DeactivatedAtUtc).IsNull();
        await Assert.That(manager.Activate(72, null, "retry-owner")).IsFalse();
        await Assert.That(manager.Remove(72)).IsFalse();
        await Assert.That(manager.GetDiagnostics().TotalDeactivations).IsEqualTo(0);
        await Assert.That(manager.GetDiagnostics().Total).IsEqualTo(manager.Count);
        await Assert.That(manager.GetDiagnostics().Deactivated).IsEqualTo(0);
        await Assert.That(manager.GetDiagnostics().Deactivating).IsEqualTo(0);
        await Assert.That(manager.GetDiagnostics().DeactivationFailed).IsEqualTo(1);
    }

    #endregion

    #region Round-trip / remove

    [Test]
    public async Task FullLifecycleRoundTrip_SpawnActivateDeactivateReactivateDeactivate_AllStatesTransition()
    {
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);

        await Assert.That(manager.Spawn(CreateCharacter(11, "round-tripper"), "owner-a")).IsTrue();
        var r1Found = manager.TryGet(11, out var r1);
        await Assert.That(r1Found).IsTrue();
        await Assert.That(r1!.State).IsEqualTo(PlayerBotState.Registered);

        await Assert.That(manager.Activate(11, null, "owner-a")).IsTrue();
        var r2Found = manager.TryGet(11, out var r2);
        await Assert.That(r2Found).IsTrue();
        await Assert.That(r2!.State).IsEqualTo(PlayerBotState.Active);

        await Assert.That(manager.Deactivate(11, "sleep")).IsTrue();
        var r3Found = manager.TryGet(11, out var r3);
        await Assert.That(r3Found).IsTrue();
        await Assert.That(r3!.State).IsEqualTo(PlayerBotState.Deactivated);

        // Reactivation from Deactivated is allowed (fidelity transitions are
        // the PopulationDirector's call; the manager just executes them).
        await Assert.That(manager.Activate(11, null, "owner-b")).IsTrue();
        var r4Found = manager.TryGet(11, out var r4);
        await Assert.That(r4Found).IsTrue();
        await Assert.That(r4!.State).IsEqualTo(PlayerBotState.Active);

        await Assert.That(manager.Deactivate(11, "shutdown")).IsTrue();
        var r5Found = manager.TryGet(11, out var r5);
        await Assert.That(r5Found).IsTrue();
        await Assert.That(r5!.State).IsEqualTo(PlayerBotState.Deactivated);

        await Assert.That(seam.ActivateCalls).HasCount(2);
        await Assert.That(seam.DeactivateCalls).HasCount(2);
    }

    [Test]
    public async Task Remove_ActiveBot_ReturnsFalse_UntilDeactivated()
    {
        var manager = new PlayerBotManager(new RecordingLifecycle());
        manager.Spawn(CreateCharacter(12), "owner-a");
        manager.Activate(12, null, "owner-a");

        // Embodied bot must not leak out of the manager.
        var refused = manager.Remove(12);
        await Assert.That(refused).IsFalse();
        await Assert.That(manager.Count).IsEqualTo(1);

        manager.Deactivate(12, "shutdown");
        var removed = manager.Remove(12);
        await Assert.That(removed).IsTrue();
        await Assert.That(manager.Count).IsEqualTo(0);
        await Assert.That(manager.TryGet(12, out _)).IsFalse();
    }

    #endregion

    #region Diagnostics

    [Test]
    public async Task Diagnostics_AfterFullLifecycle_ReflectsStateAndCounters()
    {
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);

        manager.Spawn(CreateCharacter(21), "owner-a");
        manager.Spawn(CreateCharacter(22), "owner-a");
        manager.Spawn(CreateCharacter(23), "owner-a");
        manager.Spawn(CreateCharacter(21), "owner-b");          // duplicate -> failed
        manager.Activate(21, null, "owner-a");
        manager.Activate(22, null, "owner-a");
        manager.Activate(24, null, "owner-a");                  // unknown -> failed
        manager.Deactivate(21, "sleep");
        manager.Deactivate(24, "shutdown");                     // unknown -> failed

        var d = manager.GetDiagnostics();

        await Assert.That(d.Registered).IsEqualTo(1);           // 23 never activated
        await Assert.That(d.Active).IsEqualTo(1);               // 22
        await Assert.That(d.Deactivated).IsEqualTo(1);          // 21
        await Assert.That(d.Total).IsEqualTo(3);
        await Assert.That(d.TotalSpawns).IsEqualTo(3);
        await Assert.That(d.FailedSpawns).IsEqualTo(1);
        await Assert.That(d.TotalActivations).IsEqualTo(2);
        await Assert.That(d.FailedActivations).IsEqualTo(1);
        await Assert.That(d.TotalDeactivations).IsEqualTo(1);
        await Assert.That(d.FailedDeactivations).IsEqualTo(1);
    }

    #endregion

    #region Concurrency

    [Test]
    public async Task Parallel_ActivateDistinctBots_AllSucceed_NoInterference()
    {
        const int BotCount = 50;
        var manager = new PlayerBotManager(new RecordingLifecycle());

        for (uint i = 1; i <= BotCount; i++)
            manager.Spawn(CreateCharacter(i, $"bot-{i}"), "owner-a");

        var results = await Task.WhenAll(
            Enumerable.Range(1, BotCount).Select(i => Task.Run(() => manager.Activate((uint)i, null, "owner-a"))));

        await Assert.That(results.All(r => r)).IsTrue();
        await Assert.That(manager.ActiveCount).IsEqualTo(BotCount);

        var teardowns = await Task.WhenAll(
            Enumerable.Range(1, BotCount).Select(i => Task.Run(() => manager.Deactivate((uint)i, "parallel-shutdown"))));

        await Assert.That(teardowns.All(r => r)).IsTrue();
        await Assert.That(manager.ActiveCount).IsEqualTo(0);
        await Assert.That(manager.GetDiagnostics().TotalActivations).IsEqualTo(BotCount);
        await Assert.That(manager.GetDiagnostics().TotalDeactivations).IsEqualTo(BotCount);
    }

    [Test]
    public async Task Parallel_SameBotConcurrentActivate_ExactlyOneWins()
    {
        const int Attempts = 16;
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);
        manager.Spawn(CreateCharacter(31), "owner-a");

        var results = await Task.WhenAll(
            Enumerable.Range(0, Attempts).Select(_ => Task.Run(() => manager.Activate(31, null, "owner-a"))));

        await Assert.That(results.Count(r => r)).IsEqualTo(1);
        await Assert.That(seam.ActivateCalls).HasCount(1);
        await Assert.That(manager.ActiveCount).IsEqualTo(1);
        await Assert.That(manager.GetDiagnostics().FailedActivations).IsEqualTo(Attempts - 1);
    }

    #endregion

    #region Isolation vs AIManager (review deliverable 1-C: bots never enter the NPC AI ticker)

    [Test]
    public async Task Manager_HasNoDependencyOnAIManager()
    {
        var managerType = typeof(PlayerBotManager);
        var aiTypes = new[] { typeof(AIManager), typeof(IAIManager) };

        var ctorParams = managerType.GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType);
        var fields = managerType
            .GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)
            .Select(f => f.FieldType);
        var interfaces = managerType.GetInterfaces();

        await Assert.That(ctorParams.Concat(fields).Concat(interfaces).Intersect(aiTypes)).IsEmpty();

        // The interface contract is bot-scoped too: no AI ticker surface.
        await Assert.That(typeof(IPlayerBotManager).GetInterfaces().Intersect(aiTypes)).IsEmpty();
    }

    [Test]
    public async Task Lifecycle_RoundTrip_TouchesOnlyTheLifecycleSeam()
    {
        var seam = new RecordingLifecycle();
        var manager = new PlayerBotManager(seam);
        var character = CreateCharacter(41, "seam-only");

        manager.Spawn(character, "owner-a");
        manager.Activate(41, new { step = 1 }, "owner-a");
        manager.Deactivate(41, "shutdown");

        // The only collaborator is the lifecycle seam; every transition
        // flowed through it exactly once, with the right payloads.
        await Assert.That(seam.ActivateCalls).HasCount(1);
        await Assert.That(seam.ActivateCalls[0].Character).IsEqualTo(character);
        await Assert.That(seam.DeactivateCalls).HasCount(1);
        await Assert.That(seam.DeactivateCalls[0].Character).IsEqualTo(character);
        await Assert.That(seam.DeactivateCalls[0].Reason).IsEqualTo("shutdown");

    }

    #endregion
}
