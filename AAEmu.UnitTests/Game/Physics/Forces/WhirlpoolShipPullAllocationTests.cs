using System.Numerics;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Buffs;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Slaves;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Physics.Forces;

using Jitter2;

using MySql.Data.MySqlClient;

namespace AAEmu.UnitTests.Game.Physics.Forces;

/// <summary>
/// Allocation regression for the upstream marine part8 merge (c6b5dd8f2,
/// dad07dbb3): <see cref="WhirlpoolShipPull"/> is created unconditionally by
/// PhysicsManager.Initialize() (default SeaWeatherModel=Official), so its
/// PreStep runs on the physics thread at TargetPhysicsTps (25/s) per world
/// even when no ship carries buff 1918. The pre-fix code called
/// gameWorld.GetAllSlaves() — a List&lt;Slave&gt; snapshot — on EVERY step,
/// allocating ~17 bytes per slave per step (measured: 1704 bytes/step at
/// 100 slaves). The fix scans the live dictionary non-allocatingly
/// (WorldInstance.AnySlave) and returns before any snapshot is taken.
///
/// Regression contract: per-step allocation must be FLAT in slave count (the
/// ConcurrentDictionary enumerator floor is ~64 bytes/step, constant), not
/// proportional to the number of slaves. The buff probe uses an
/// allocation-free IBuffs stub: the real Buffs.CheckBuff allocates
/// (_effects.ToArray() + ToList()) per call even when empty, which would
/// pollute the measurement.
/// </summary>
[NotInParallel]
public class WhirlpoolShipPullAllocationTests
{
    private const int StepIterations = 50_000;

    [Test]
    public async Task PreStep_NoBuffedShips_AllocationIsFlatInSlaveCount()
    {
        var small = MeasureNoBuffSteps(SlaveCount: 8);
        var large = MeasureNoBuffSteps(SlaveCount: 100);

        // Old code: List<Slave> snapshot per step → allocation scales with
        // slave count (8 slaves ≈ 136 B/step vs 100 slaves ≈ 1704 B/step).
        // Fixed: only the constant ConcurrentDictionary enumerator floor
        // (~64 B/step) remains — flat regardless of slave count.
        var perStepSmall = small / (double)StepIterations;
        var perStepLarge = large / (double)StepIterations;
        await Assert.That(perStepLarge - perStepSmall < 32.0)
            .IsTrue()
            .Because($"no-buff PreStep allocation must be flat in slave count (old code: ~17 B/slave/step); small={perStepSmall:F1} B/step, large={perStepLarge:F1} B/step");
    }

    private static long MeasureNoBuffSteps(int SlaveCount)
    {
        var world = new WorldInstance(new WorldTemplate { Id = 7, Name = "whirlpool-test" }, 0, true, 7);
        for (uint i = 1; i <= SlaveCount; i++)
        {
            var slave = new Slave
            {
                ObjId = i,
                Template = new SlaveTemplate { Id = 15, ModelId = 129 },
                Buffs = new NoBuffBuffs(), // allocation-free CheckBuff(1918) == false
            };
            slave.Transform.Local.SetPosition(new Vector3(i * 10f, 0f, 0f));
            world.AddObject(slave);
        }

        using var jitter = new Jitter2.World();
        var pull = new WhirlpoolShipPull(jitter, () => world);

        // Warm up (JIT tiering + dictionary shape).
        for (var i = 0; i < 1_000; i++)
            pull.PreStep(0.04f);

        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < StepIterations; i++)
            pull.PreStep(0.04f);
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>IBuffs stub whose CheckBuff returns false without allocating.</summary>
    private sealed class NoBuffBuffs : IBuffs
    {
        public bool CheckBuff(uint id) => false;
        public void AddBuff(Buff buff, uint index = 0, int forcedDuration = 0) { }
        public void AddBuff(uint buffId, BaseUnit caster, int forcedDuration = 0) { }
        public bool CheckBuffImmune(uint buffId) => false;
        public bool CheckBuffs(List<uint> ids) => false;
        public bool CheckBuffsExcludingTags(List<uint> ids, uint[] excludedTagIds) => false;
        public bool CheckBuffTag(uint tagId) => false;
        public bool CheckDamageImmune(DamageType damageType) => false;
        public IEnumerable<Buff> GetAbsorptionEffects() => [];
        public void GetAllBuffs(List<Buff> goodBuffs, List<Buff> badBuffs, List<Buff> hiddenBuffs, bool includeAllPassives) { }
        public int GetBuffCountById(uint buffId) => 0;
        public IEnumerable<Buff> GetBuffsRequiring(uint buffId) => [];
        public Buff GetEffectByIndex(uint index) => null;
        public Buff GetEffectByTemplate(BuffTemplate template) => null;
        public Buff GetEffectFromBuffId(uint id) => null;
        public List<Buff> GetEffectsByType(Type effectType) => [];
        public bool HasEffectsMatchingCondition(Func<Buff, bool> predicate) => false;
        public void RemoveAllEffects() { }
        public void RemoveBuff(uint buffId) { }
        public void RemoveBuffs(BuffKind kind, int count, uint buffTagId = 0) { }
        public void RemoveBuffs(uint buffTagId, int count) { }
        public void RemoveEffect(Buff buff) { }
        public void RemoveEffect(uint index) { }
        public void RemoveEffect(uint templateId, uint skillId) { }
        public void RemoveEffectsOnDeath() { }
        public void RemoveStealth() { }
        public void SetOwner(BaseUnit owner) { }
        public void TriggerRemoveOn(BuffRemoveOn on, uint value = 0) { }
        public void SaveActiveBuffs(MySqlConnection connection, MySqlTransaction transaction, uint characterId) { }
        public void LoadActiveBuffs(Character character) { }
        public void CancelAllEffectTasks() { }
    }
}
