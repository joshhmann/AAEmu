#nullable enable

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Loot;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Runtime brain-tuning slice: a single process-wide snapshot layered over the
/// canonical brain defaults. No override → byte-identical behavior; valid
/// update → next wake reads the new value; invalid → rejected, never silently
/// accepted; revision visible on the wake result and the inspector snapshot.
/// </summary>
[NotInParallel]
public class BotBrainTuningTests
{
    [Before(HookType.Test)]
    public void ResetTuning()
    {
        BotBrainTuningStore.ResetForTests();
        QuestBehavior.ClearGiveUpMemory();
        QuestBehavior.ClearCombatStallMemory();
        QuestBehavior.ClearLeashStaleMemory();
    }

    [After(HookType.Test)]
    public void RestoreTuning()
    {
        BotBrainTuningStore.ResetForTests();
        QuestBehavior.ClearGiveUpMemory();
        QuestBehavior.ClearCombatStallMemory();
        QuestBehavior.ClearLeashStaleMemory();
    }

    [Test]
    public async Task DefaultsUnchanged_WhenNoOverride()
    {
        await Assert.That(BotBrainTuningStore.Revision).IsEqualTo(0L);
        await Assert.That(BotBrainTuningStore.Current).IsEqualTo(BotBrainTuning.Empty);
        await Assert.That(BotBrainTuningStore.ResolveCombatStallWakes()).IsEqualTo(QuestBehavior.CombatStallWakes);
        await Assert.That(BotBrainTuningStore.ResolveLeashEdgeFraction()).IsEqualTo(CombatBrainEngagement.LeashEdgeFraction);

        var opts = BotBrainTuningStore.ApplyTo(new QuestDecisionScenario.QuestOptions());
        await Assert.That(opts.LootSafeRadiusM).IsEqualTo(LootBrain.DefaultSafeRadiusM);
        await Assert.That(opts.GiveUpAfterUnresolvedWakes).IsEqualTo(10);
        await Assert.That(opts.GiveUpAfterPlanFailures).IsEqualTo(10);
    }

    [Test]
    public async Task ValidUpdate_Succeeds_PartialKeepsRest()
    {
        var patch = BotBrainTuningStore.ParsePatch("""{"lootSafeRadiusM": 5}""");
        await Assert.That(BotBrainTuning.Validate(patch)).IsNull();
        var (tuning, revision, changed) = BotBrainTuningStore.Apply(patch);
        await Assert.That(changed).IsTrue();
        await Assert.That(revision).IsEqualTo(1L);
        await Assert.That(tuning.LootSafeRadiusM).IsEqualTo(5f);
        await Assert.That(tuning.GiveUpAfterUnresolvedWakes).IsNull();

        var view = BotBrainTuningStore.Snapshot();
        await Assert.That(view.Revision).IsEqualTo(1L);
        var safe = view.Fields.Single(f => f.Field == "lootSafeRadiusM");
        await Assert.That(safe.EffectiveValue).IsEqualTo(5f.ToString("F1"));
        await Assert.That(safe.OverrideValue).IsEqualTo(5f.ToString("F1"));
    }

    [Test]
    public async Task InvalidUpdate_Rejected_NeverSilentlyAccepted()
    {
        foreach (var body in new[]
        {
            """{"lootSafeRadiusM": -1}""",
            """{"lootSafeRadiusM": 1000}""",
            """{"lootSafeRadiusM": NaN}""",
            """{"giveUpAfterUnresolvedWakes": -1}""",
            """{"giveUpAfterUnresolvedWakes": 1001}""",
            """{"combatStallWakes": -5}""",
            """{"leashEdgeFraction": 0}""",
            """{"leashEdgeFraction": 5}""",
        })
        {
            BotBrainTuningPatch patch;
            try
            {
                patch = BotBrainTuningStore.ParsePatch(body);
            }
            catch (Exception)
            {
                continue; // malformed JSON is itself a rejection — also fine
            }
            await Assert.That(BotBrainTuning.Validate(patch)).IsNotNull();
        }
        await Assert.That(BotBrainTuningStore.Revision).IsEqualTo(0L);
        await Assert.That(BotBrainTuningStore.Current).IsEqualTo(BotBrainTuning.Empty);
    }

    [Test]
    public async Task NextWake_ReadsNewValue()
    {
        var (tuning, revision, _) = BotBrainTuningStore.Apply(
            BotBrainTuningStore.ParsePatch("""{"combatStallWakes": 3, "leashEdgeFraction": 0.5}"""));
        await Assert.That(revision).IsEqualTo(1L);
        await Assert.That(BotBrainTuningStore.ResolveCombatStallWakes()).IsEqualTo(3);
        await Assert.That(BotBrainTuningStore.ResolveLeashEdgeFraction()).IsEqualTo(0.5f);

        var opts = BotBrainTuningStore.ApplyTo(new QuestDecisionScenario.QuestOptions());
        await Assert.That(opts.LootSafeRadiusM).IsEqualTo(LootBrain.DefaultSafeRadiusM);

        var (_, _, changed) = BotBrainTuningStore.Apply(
            BotBrainTuningStore.ParsePatch("""{"lootSafeRadiusM": 5}"""));
        await Assert.That(changed).IsTrue();
        opts = BotBrainTuningStore.ApplyTo(new QuestDecisionScenario.QuestOptions());
        await Assert.That(opts.LootSafeRadiusM).IsEqualTo(5f);
        // Earlier overrides survive a partial update.
        await Assert.That(opts.GiveUpAfterUnresolvedWakes).IsEqualTo(10);
        await Assert.That(BotBrainTuningStore.ResolveCombatStallWakes()).IsEqualTo(3);
        _ = tuning;
    }

    [Test]
    public async Task ExplicitNull_ClearsOverride_BackToDefault()
    {
        _ = BotBrainTuningStore.Apply(BotBrainTuningStore.ParsePatch("""{"lootSafeRadiusM": 5}"""));
        await Assert.That(BotBrainTuningStore.Revision).IsEqualTo(1L);
        var (_, revision, changed) = BotBrainTuningStore.Apply(
            BotBrainTuningStore.ParsePatch("""{"lootSafeRadiusM": null}"""));
        await Assert.That(changed).IsTrue();
        await Assert.That(revision).IsEqualTo(2L);
        var opts = BotBrainTuningStore.ApplyTo(new QuestDecisionScenario.QuestOptions());
        await Assert.That(opts.LootSafeRadiusM).IsEqualTo(LootBrain.DefaultSafeRadiusM);
    }

    [Test]
    public async Task StallThreshold_OverrideTripsEarlier()
    {
        const uint actor = 0x7E01u;
        const uint target = 0x7E02u;
        // Canonical default (10): 9 wakes do not trip.
        for (var i = 0; i < QuestBehavior.CombatStallWakes - 1; i++)
            await Assert.That(QuestBehavior.CountCombatStallWake(actor, target, 100, $"d-{i}", out _)).IsFalse();
        QuestBehavior.ClearCombatStallMemory();

        _ = BotBrainTuningStore.Apply(BotBrainTuningStore.ParsePatch("""{"combatStallWakes": 3}"""));
        await Assert.That(QuestBehavior.CountCombatStallWake(actor, target, 100, "t-0", out _)).IsFalse();
        await Assert.That(QuestBehavior.CountCombatStallWake(actor, target, 100, "t-1", out _)).IsFalse();
        await Assert.That(QuestBehavior.CountCombatStallWake(actor, target, 100, "t-2", out var wakes)).IsTrue();
        await Assert.That(wakes).IsEqualTo(3);
    }

    [Test]
    public async Task Revision_OutputReflectsUpdate_OnSnapshot()
    {
        var before = BotBrainTuningStore.Snapshot();
        await Assert.That(before.Revision).IsEqualTo(0L);
        _ = BotBrainTuningStore.Apply(BotBrainTuningStore.ParsePatch("""{"giveUpAfterUnresolvedWakes": 4}"""));
        var after = BotBrainTuningStore.Snapshot();
        await Assert.That(after.Revision).IsEqualTo(1L);
        var field = after.Fields.Single(f => f.Field == "giveUpAfterUnresolvedWakes");
        await Assert.That(field.EffectiveValue).IsEqualTo("4");
        await Assert.That(field.PreviousValue).IsEqualTo("default");
        await Assert.That(field.ObservedAtUtc).IsNotNull();
    }
}
