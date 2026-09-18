using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Skills.Templates;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Step-6 bounded combat executor tests (ROADMAP P1 "Bounded combat executor
/// prerequisite: learned-skill GCD").
///
/// What is proven here, against the REAL engine gate
/// (<c>Skill.Use(..., bypassGcd: false)</c> — the shape
/// <c>CSStartSkillPacket</c>'s learned-skill branch passes):
///  - the non-bypass learned-skill gate is actually entered (GlobalCooldown /
///    SkillLastUsed / per-skill cooldown arm), unlike the bypassing
///    <see cref="IGameplayActor.Cast"/> path, which stays single-shot per the
///    revert;
///  - a stationary executor clock spends ZERO retry attempts (the reverted
///    actor-level enforcement failed exactly here: no-time-advance runners
///    burned hunt budget on stationary refusals);
///  - a timing refusal (<see cref="SkillResult.CooldownTime"/>) is retried
///    after an executor-owned wait and completes once the engine gate opens
///    (real short-cooldown integration — the engine clock is wall-clock and
///    uninjectable, so the executor's own clock owns the wait decision);
///  - a non-timing refusal is authoritative and terminal, and an exhausted
///    attempt/budget bound is terminal WITHOUT spending a further attempt;
///  - a repeat of an explicit idempotency key whose effect already applied is
///    answered from the effect ledger with zero engine entries (no duplicated
///    effect).
///
/// H stays UNKNOWN (never inferred from scripted evidence).
/// </summary>
[NotInParallel]
public class CombatExecutorTests
{
    /// <summary>Skill on the GCD arm: real 1s global cooldown, no per-skill cooldown.</summary>
    private const uint GcdSkillId = 90011;

    /// <summary>Skill on the per-skill cooldown arm: long cooldown, no global cooldown.</summary>
    private const uint CooldownSkillId = 90012;

    /// <summary>Skill on the per-skill cooldown arm with a short (real-time) expiry.</summary>
    private const uint ShortCooldownSkillId = 90013;

    /// <summary>
    /// E1 (client-branch parity + revert stands): the executor's engine call
    /// arms the learned-skill GCD state (GlobalCooldown + SkillLastUsed),
    /// which is only reachable with <c>bypassGcd: false</c> — the same gate
    /// the client's learned-skill packet branch drives. <see cref="GameplayActor.Cast"/>
    /// keeps its unchanged bypass behavior (no arming), so the reverted
    /// actor-level enforcement is not reintroduced.
    /// </summary>
    [Test]
    public async Task ExecutorRidesLearnedSkillGate_AndCastRemainsUnchanged()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("combat-exec-gcd");
        var skillId = SeedSkill(actor, GcdSkillId, defaultGcd: true, cooldownTime: 0);
        var target = actor.ActorId;
        actor.Character.GlobalCooldown = default;
        actor.Character.SkillLastUsed = default;

        var executor = new CombatExecutor();
        var outcome = executor.Begin(actor.Character, skillId, target);

        await Assert.That(outcome.Status).IsEqualTo(CombatCastStatus.Completed);
        await Assert.That(outcome.EngineResult).IsEqualTo(SkillResult.Success);
        await Assert.That(outcome.Attempts).IsEqualTo(1);

        // Engine effect of riding the non-bypass gate: the GCD was stamped
        // (Skill.Cast: GlobalCooldown = now + gcd when _bypassGcd is false).
        await Assert.That(actor.Character.GlobalCooldown > DateTime.UtcNow).IsTrue();
        await Assert.That(actor.Character.SkillLastUsed > DateTime.MinValue).IsTrue();

        // The actor path is untouched: it bypasses (Unit.UseSkill's
        // bypassGcd: true), so a Cast does NOT stamp the GCD.
        actor.Character.GlobalCooldown = default;
        actor.Character.SkillLastUsed = default;
        var cast = actor.Cast(skillId, target);
        await Assert.That(cast.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(actor.Character.GlobalCooldown).IsEqualTo(default(DateTime));
        await Assert.That(actor.Character.SkillLastUsed).IsEqualTo(default(DateTime));
    }

    /// <summary>
    /// E2 (stationary-time acceptance): a logical cast whose first attempt is
    /// refused with <see cref="SkillResult.CooldownTime"/> reports
    /// <see cref="CombatCastStatus.Waiting"/> and burns NO further attempt
    /// while the executor clock is stationary — the engine is never re-entered.
    /// </summary>
    [Test]
    public async Task StationaryClock_SpendsNoRetryAttempts()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("combat-exec-stationary");
        var skillId = SeedSkill(actor, CooldownSkillId, defaultGcd: false, cooldownTime: 3000);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            DateTimeOffset.Parse("2026-09-17T09:00:00Z"));
        var executor = new CombatExecutor { TimeProvider = clock };

        // Prime the engine's per-skill cooldown with one real cast.
        var prime = executor.Begin(actor.Character, skillId, actor.ActorId);
        await Assert.That(prime.Status).IsEqualTo(CombatCastStatus.Completed);
        var entriesAfterPrime = executor.EngineEntries;

        var outcome = executor.Begin(actor.Character, skillId, actor.ActorId);
        await Assert.That(outcome.Status).IsEqualTo(CombatCastStatus.Waiting);
        await Assert.That(outcome.EngineResult).IsEqualTo(SkillResult.CooldownTime);
        await Assert.That(outcome.Attempts).IsEqualTo(1);
        await Assert.That(outcome.NextAttemptAtUtc).IsEqualTo(clock.GetUtcNow().UtcDateTime.Add(executor.RetryBackoff));

        // Time stationary: repeated ticks never reach the engine.
        for (var i = 0; i < 5; i++)
        {
            var again = executor.Tick();
            await Assert.That(again.Status).IsEqualTo(CombatCastStatus.Waiting);
            await Assert.That(again.Attempts).IsEqualTo(1);
            await Assert.That(again.EngineResult).IsEqualTo(SkillResult.CooldownTime);
        }
        await Assert.That(executor.EngineEntries).IsEqualTo(entriesAfterPrime + 1);

        // Advancing the executor clock by less than the wait still spends nothing.
        clock.Advance(TimeSpan.FromMilliseconds(149));
        var early = executor.Tick();
        await Assert.That(early.Status).IsEqualTo(CombatCastStatus.Waiting);
        await Assert.That(early.Attempts).IsEqualTo(1);
        await Assert.That(executor.EngineEntries).IsEqualTo(entriesAfterPrime + 1);
    }

    /// <summary>
    /// E3 (bounded retry + authoritative terminal refusal): once the executor
    /// wait has elapsed the attempt is spent; the attempt bound is terminal
    /// and spends no further engine entry.
    /// </summary>
    [Test]
    public async Task RetryBudgetExhaustion_IsTerminalRefusal_WithoutExtraAttempts()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("combat-exec-budget");
        var skillId = SeedSkill(actor, CooldownSkillId, defaultGcd: false, cooldownTime: 3000);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            DateTimeOffset.Parse("2026-09-17T09:00:00Z"));
        var executor = new CombatExecutor { TimeProvider = clock, MaxAttempts = 3 };

        var prime = executor.Begin(actor.Character, skillId, actor.ActorId);
        await Assert.That(prime.Status).IsEqualTo(CombatCastStatus.Completed);
        var entriesAfterPrime = executor.EngineEntries;

        var first = executor.Begin(actor.Character, skillId, actor.ActorId);
        await Assert.That(first.Attempts).IsEqualTo(1);

        CombatCastOutcome? last = null;
        for (var i = 0; i < 3; i++)
        {
            clock.Advance(executor.RetryBackoff);
            last = executor.Tick();
            if (last.IsTerminal)
                break;
        }

        await Assert.That(last!.Status).IsEqualTo(CombatCastStatus.Refused);
        await Assert.That(last.Attempts).IsEqualTo(3);
        await Assert.That(last.EngineResult).IsEqualTo(SkillResult.CooldownTime);
        await Assert.That(last.Detail?.Contains("retry budget exhausted")).IsTrue();
        await Assert.That(executor.EngineEntries).IsEqualTo(entriesAfterPrime + 3);

        // Terminal: further ticks are Idle and add no engine entry.
        clock.Advance(TimeSpan.FromSeconds(30));
        var afterTerminal = executor.Tick();
        await Assert.That(afterTerminal.Status).IsEqualTo(CombatCastStatus.Idle);
        await Assert.That(executor.EngineEntries).IsEqualTo(entriesAfterPrime + 3);
        await Assert.That(executor.IsPending).IsFalse();
    }

    /// <summary>
    /// E4 (success after wait): a timing refusal followed by the engine gate
    /// actually opening completes the logical cast after exactly two engine
    /// entries — the executor waited, the engine accepted.
    /// </summary>
    [Test]
    public async Task TimingRefusal_CompletesAfterExecutorWait_WhenGateOpens()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("combat-exec-wait");
        // 700ms real engine cooldown: still armed at the first attempt,
        // expired (<=250ms remaining clears it) after a 600ms real wait.
        var skillId = SeedSkill(actor, ShortCooldownSkillId, defaultGcd: false, cooldownTime: 700);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            DateTimeOffset.Parse("2026-09-17T09:00:00Z"));
        var executor = new CombatExecutor { TimeProvider = clock };

        var prime = executor.Begin(actor.Character, skillId, actor.ActorId);
        await Assert.That(prime.Status).IsEqualTo(CombatCastStatus.Completed);
        var entriesAfterPrime = executor.EngineEntries;

        var refused = executor.Begin(actor.Character, skillId, actor.ActorId);
        await Assert.That(refused.Status).IsEqualTo(CombatCastStatus.Waiting);
        await Assert.That(refused.EngineResult).IsEqualTo(SkillResult.CooldownTime);

        // The engine clock is wall-clock (uninjectable): let the real short
        // cooldown lapse, and advance the executor clock past its own wait.
        Thread.Sleep(600);
        clock.Advance(executor.RetryBackoff);

        var done = executor.Tick();
        await Assert.That(done.Status).IsEqualTo(CombatCastStatus.Completed);
        await Assert.That(done.EngineResult).IsEqualTo(SkillResult.Success);
        await Assert.That(done.Attempts).IsEqualTo(2);
        await Assert.That(done.NextAttemptAtUtc).IsNull();
        await Assert.That(executor.EngineEntries).IsEqualTo(entriesAfterPrime + 2);
        // The accepted cast landed a real engine effect (cooldown re-armed).
        await Assert.That(actor.Character.Cooldowns.CheckCooldown(skillId)).IsTrue();
    }

    /// <summary>
    /// E5 (no duplicated effect): repeating an explicit key whose effect
    /// already applied is answered from the effect ledger with ZERO engine
    /// entries — the engine cannot be re-entered by a retry.
    /// </summary>
    [Test]
    public async Task RepeatedKey_SuppressesDuplicateEffect()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("combat-exec-dedupe");
        var skillId = SeedSkill(actor, GcdSkillId, defaultGcd: true, cooldownTime: 0);
        var executor = new CombatExecutor();

        var first = executor.Begin(actor.Character, skillId, actor.ActorId, idempotencyKey: "combat:swing-1");
        await Assert.That(first.Status).IsEqualTo(CombatCastStatus.Completed);
        await Assert.That(first.DuplicateSuppressed).IsFalse();
        await Assert.That(executor.EngineEntries).IsEqualTo(1);
        await Assert.That(executor.Ledger.EffectCount).IsEqualTo(1);

        var retry = executor.Begin(actor.Character, skillId, actor.ActorId, idempotencyKey: "combat:swing-1");
        await Assert.That(retry.Status).IsEqualTo(CombatCastStatus.Completed);
        await Assert.That(retry.DuplicateSuppressed).IsTrue();
        await Assert.That(retry.Attempts).IsEqualTo(0);
        await Assert.That(retry.Detail?.Contains("effect already applied")).IsTrue();
        await Assert.That(executor.EngineEntries).IsEqualTo(1);
        await Assert.That(executor.Ledger.EffectCount).IsEqualTo(1);
        await Assert.That(executor.IsPending).IsFalse();
    }

    /// <summary>
    /// E6 (authoritative refusal, zero budget burn): a request the learned-
    /// skill branch cannot route (unknown skill, unlearned skill, missing
    /// target) is refused terminally without entering the engine at all.
    /// </summary>
    [Test]
    public async Task UnroutableRequest_RefusedWithoutEngineEntry()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("combat-exec-refuse");
        var skillId = SeedSkill(actor, GcdSkillId, defaultGcd: true, cooldownTime: 0);
        var executor = new CombatExecutor();

        var unknownTarget = executor.Begin(actor.Character, skillId, 0xDEAD_BEEF);
        await Assert.That(unknownTarget.Status).IsEqualTo(CombatCastStatus.Refused);
        await Assert.That(unknownTarget.Attempts).IsEqualTo(0);
        await Assert.That(unknownTarget.Detail?.Contains("not found in world")).IsTrue();

        var unknownSkill = executor.Begin(actor.Character, 999_999, actor.ActorId);
        await Assert.That(unknownSkill.Status).IsEqualTo(CombatCastStatus.Refused);
        await Assert.That(unknownSkill.Attempts).IsEqualTo(0);
        await Assert.That(unknownSkill.Detail?.Contains("unknown skill")).IsTrue();

        // Learning gate: a real template the character has NOT learned. Its
        // AbilityLevel differs from every learned skill's, so the engine's
        // variant rule (IsVariantOfSkill matches same ability + level)
        // cannot claim it as known.
        var unlearned = SeedTemplateOnly(GcdSkillId + 100, abilityLevel: 3);
        var notLearned = executor.Begin(actor.Character, unlearned.Id, actor.ActorId);
        await Assert.That(notLearned.Status).IsEqualTo(CombatCastStatus.Refused);
        await Assert.That(notLearned.Attempts).IsEqualTo(0);
        await Assert.That(notLearned.Detail?.Contains("not learned")).IsTrue();

        await Assert.That(executor.EngineEntries).IsEqualTo(0);
    }

    /// <summary>
    /// E9 (one logical cast in flight): a second Begin while a cast is
    /// waiting is refused with zero engine entries and does not disturb the
    /// pending cast's wait or attempt count.
    /// </summary>
    [Test]
    public async Task SecondBeginWhileWaiting_RefusedWithoutDisturbingPendingCast()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("combat-exec-busy");
        var skillId = SeedSkill(actor, CooldownSkillId, defaultGcd: false, cooldownTime: 3000);
        var otherSkillId = SeedSkill(actor, GcdSkillId, defaultGcd: true, cooldownTime: 0);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            DateTimeOffset.Parse("2026-09-17T09:00:00Z"));
        var executor = new CombatExecutor { TimeProvider = clock };

        var prime = executor.Begin(actor.Character, skillId, actor.ActorId);
        await Assert.That(prime.Status).IsEqualTo(CombatCastStatus.Completed);
        var entriesAfterPrime = executor.EngineEntries;

        var pending = executor.Begin(actor.Character, skillId, actor.ActorId);
        await Assert.That(pending.Status).IsEqualTo(CombatCastStatus.Waiting);

        var busy = executor.Begin(actor.Character, otherSkillId, actor.ActorId);
        await Assert.That(busy.Status).IsEqualTo(CombatCastStatus.Refused);
        await Assert.That(busy.Attempts).IsEqualTo(0);
        await Assert.That(busy.Detail?.Contains("executor busy")).IsTrue();
        await Assert.That(executor.EngineEntries).IsEqualTo(entriesAfterPrime + 1);
        await Assert.That(executor.IsPending).IsTrue();

        // The pending cast still owns its own wait.
        var stillPending = executor.Tick();
        await Assert.That(stillPending.Status).IsEqualTo(CombatCastStatus.Waiting);
        await Assert.That(stillPending.SkillId).IsEqualTo(skillId);
        await Assert.That(stillPending.Attempts).IsEqualTo(1);
        await Assert.That(executor.EngineEntries).IsEqualTo(entriesAfterPrime + 1);

        // Cancel drops it without further engine entries.
        executor.Cancel();
        await Assert.That(executor.IsPending).IsFalse();
        await Assert.That(executor.Tick().Status).IsEqualTo(CombatCastStatus.Idle);
        await Assert.That(executor.EngineEntries).IsEqualTo(entriesAfterPrime + 1);
    }

    /// <summary>
    /// E7 (client packet path stays covered): the executor and the client's
    /// learned-skill branch consult the SAME engine gate state, proven
    /// bidirectionally —
    ///  1. a cast the executor completed consumed the template's engine
    ///     cooldown, so the test's own packet-shaped call
    ///     (<c>Skill.Use(..., bypassGcd: false)</c> with the
    ///     <c>SkillCasterUnit</c>/<c>SkillCastUnitTarget</c> pair
    ///     <c>CSStartSkillPacket</c> passes) is refused with
    ///     <see cref="SkillResult.CooldownTime"/>;
    ///  2. a cast the packet-shaped call completed leaves the executor's next
    ///     attempt on a second skill id refused by the same gate — the
    ///     executor reports <see cref="CombatCastStatus.Waiting"/>, not a
    ///     success.
    /// A private, bot-only bypass would break both directions. The existing
    /// <c>GameplayActorM53CoreSurfaceTests</c> suite continues to cover the
    /// packet-shaped actor path; no packet is fabricated or modified here.
    /// (The GCD arm's anti-hang throttle is call-site stateful — it lets the
    /// first rapid re-cast through — so this parity proof rides the
    /// template-cooldown arm, which is deterministic; E1 covers the
    /// bypass/no-bypass distinction itself.)
    /// </summary>
    [Test]
    public async Task ExecutorAndClientBranchShareTheSameGate()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("combat-exec-clientpath");
        var executorSkillId = SeedSkill(actor, CooldownSkillId, defaultGcd: false, cooldownTime: 3000);
        var clientSkillId = SeedSkill(actor, CooldownSkillId + 1, defaultGcd: false, cooldownTime: 3000);
        var executor = new CombatExecutor();

        // 1. Executor completes a cast, consuming the engine cooldown.
        var byExecutor = executor.Begin(actor.Character, executorSkillId, actor.ActorId);
        await Assert.That(byExecutor.Status).IsEqualTo(CombatCastStatus.Completed);

        // The client-shaped call reads the very same engine cooldown state.
        await Assert.That(CastLikeClientLearnedSkillBranch(actor, executorSkillId))
            .IsEqualTo(SkillResult.CooldownTime);

        // 2. The client-shaped call completes a cast on another skill id; the
        //    executor then reads that consumed cooldown instead of firing.
        await Assert.That(CastLikeClientLearnedSkillBranch(actor, clientSkillId))
            .IsEqualTo(SkillResult.Success);

        var byExecutorAgain = executor.Begin(actor.Character, clientSkillId, actor.ActorId);
        await Assert.That(byExecutorAgain.Status).IsEqualTo(CombatCastStatus.Waiting);
        await Assert.That(byExecutorAgain.EngineResult).IsEqualTo(SkillResult.CooldownTime);
        await Assert.That(byExecutorAgain.Attempts).IsEqualTo(1);
        await Assert.That(executor.EngineEntries).IsEqualTo(2);
    }

    /// <summary>
    /// E8 (wait budget is a real bound): with attempts still available, a
    /// clock that advances past the wait budget ends the logical cast as a
    /// terminal refusal instead of waiting forever — and the refusal is
    /// reported with the engine's timing result, not a fabricated failure.
    /// </summary>
    [Test]
    public async Task WaitBudgetExpiry_IsTerminalRefusal()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("combat-exec-budget-time");
        var skillId = SeedSkill(actor, CooldownSkillId, defaultGcd: false, cooldownTime: 3000);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(
            DateTimeOffset.Parse("2026-09-17T09:00:00Z"));
        var executor = new CombatExecutor
        {
            TimeProvider = clock,
            MaxAttempts = 10,
            WaitBudget = TimeSpan.FromMilliseconds(300)
        };

        var prime = executor.Begin(actor.Character, skillId, actor.ActorId);
        await Assert.That(prime.Status).IsEqualTo(CombatCastStatus.Completed);

        var waiting = executor.Begin(actor.Character, skillId, actor.ActorId);
        await Assert.That(waiting.Status).IsEqualTo(CombatCastStatus.Waiting);
        var entriesWhenWaiting = executor.EngineEntries;

        clock.Advance(TimeSpan.FromMilliseconds(200));
        var second = executor.Tick();
        await Assert.That(second.Status).IsEqualTo(CombatCastStatus.Waiting);
        await Assert.That(second.Attempts).IsEqualTo(2);

        clock.Advance(TimeSpan.FromMilliseconds(150)); // 350ms total > 300ms budget
        var expired = executor.Tick();
        await Assert.That(expired.Status).IsEqualTo(CombatCastStatus.Refused);
        await Assert.That(expired.Attempts).IsEqualTo(2); // no attempt spent on expiry
        await Assert.That(expired.NextAttemptAtUtc).IsNull();
        await Assert.That(expired.Detail?.Contains("wait budget")).IsTrue();
        await Assert.That(expired.Detail?.Contains("CooldownTime")).IsTrue();
        await Assert.That(executor.EngineEntries).IsEqualTo(entriesWhenWaiting + 1);

        var after = executor.Tick();
        await Assert.That(after.Status).IsEqualTo(CombatCastStatus.Idle);
    }

    /// <summary>
    /// The client's learned-skill branch call shape, executed verbatim:
    /// <c>new Skill(template)</c> + <c>SkillCasterUnit</c> +
    /// <c>SkillCastUnitTarget</c> + <c>bypassGcd: false</c> — the same
    /// arguments <c>CSStartSkillPacket</c> passes for a learned skill.
    /// </summary>
    private static SkillResult CastLikeClientLearnedSkillBranch(GameplayActor actor, uint skillId)
    {
        var skill = new AAEmu.Game.Models.Game.Skills.Skill(
            SkillManager.Instance.GetSkillTemplate(skillId), actor.Character);
        var casterCaster = AAEmu.Game.Models.Game.Skills.SkillCaster.GetByType(
            AAEmu.Game.Models.Game.Skills.SkillCasterType.Unit);
        casterCaster.ObjId = actor.ActorId;
        var targetCaster = AAEmu.Game.Models.Game.Skills.SkillCastTarget.GetByType(
            AAEmu.Game.Models.Game.Skills.SkillCastTargetType.Unit);
        targetCaster.ObjId = actor.ActorId;
        return skill.Use(actor.Character, casterCaster, targetCaster, null, false, out _);
    }

    #region Seeding

    /// <summary>
    /// Seeds a template with the requested gate shape (additive, missing-only)
    /// and learns it on the actor — the same real Character.Skills gate the
    /// client's learned-skill branch uses.
    /// </summary>
    private static uint SeedSkill(GameplayActor actor, uint skillId, bool defaultGcd, int cooldownTime)
    {
        var template = SeedTemplateOnly(skillId, defaultGcd, cooldownTime);
        if (!actor.Character.Skills.Skills.ContainsKey(template.Id))
            actor.Character.Skills.AddSkill(template, 1, false);
        return template.Id;
    }

    /// <summary>Seeds only the SkillManager template (no learn) — additive, missing-only.</summary>
    private static SkillTemplate SeedTemplateOnly(uint skillId, bool defaultGcd = false, int cooldownTime = 0, int abilityLevel = 0)
    {
        var manager = SkillManager.Instance;
        var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var skills = (Dictionary<uint, SkillTemplate>)typeof(SkillManager).GetField("_skills", flags)!.GetValue(manager)!;
        if (!skills.TryGetValue(skillId, out var template))
        {
            template = new SkillTemplate
            {
                Id = skillId,
                AbilityLevel = abilityLevel,
                ManaCost = 0,
                CastingTime = 0,
                CooldownTime = cooldownTime,
                DefaultGcd = defaultGcd,
                MinRange = 0,
                MaxRange = 100,
                TargetType = AAEmu.Game.Models.Game.Skills.SkillTargetType.Self,
                TargetSelection = AAEmu.Game.Models.Game.Skills.SkillTargetSelection.Target
            };
            skills[skillId] = template;
        }
        return template;
    }

    #endregion
}
