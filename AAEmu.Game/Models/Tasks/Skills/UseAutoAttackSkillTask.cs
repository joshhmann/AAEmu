using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Tasks.Skills;

public class UseAutoAttackSkillTask : SkillTask
{
    private readonly Character _caster;
    private readonly Skill _mainhandSkill;

    /// <summary>
    /// Cached offhand auto-attack skill instance. Template never changes, so we build
    /// it on demand once and reuse. Whether it actually FIRES per tick is decided
    /// fresh in Execute() based on current offhand equipment — see ResolveOffhandSkill().
    /// </summary>
    private Skill _offhandSkill;
    private SkillTemplate _offhandSkillTemplate;

    // The actor opts in; ordinary player attacks allocate no diagnostic rows.
    // Publish one immutable reference so the inspector never sees a partial tick.
    internal sealed record TickObservation(DateTime Utc, uint TargetId, string State,
        SkillResult? Result, float? Distance, float? MaxRange);
    private TickObservation _lastTick;
    private bool _observeTicks;
    internal bool ObserveTicks
    {
        get => Volatile.Read(ref _observeTicks);
        set => Volatile.Write(ref _observeTicks, value);
    }
    internal TickObservation LastTick => Volatile.Read(ref _lastTick);

    private void Observe(string state, uint targetId, SkillResult? result = null,
        float? distance = null, float? maxRange = null)
    {
        if (ObserveTicks)
            Volatile.Write(ref _lastTick, new TickObservation(DateTime.UtcNow, targetId, state, result, distance, maxRange));
    }

    public UseAutoAttackSkillTask(Skill skill, Character caster) : base(skill)
    {
        _mainhandSkill = skill;
        _caster = caster;
        Cancelled = false;
    }

    public override void Execute()
    {
        // Cancellation cannot retract a worker already queued by TaskManager.
        // An obsolete callback must neither strike nor stop its replacement.
        if (!ReferenceEquals(_caster.AutoAttackTask, this))
        {
            Observe("superseded", _caster.CurrentTarget?.ObjId ?? 0);
            Cancelled = true;
            Cancel();
            return;
        }
        var target = _caster.CurrentTarget as Unit;

        // Stop conditions: dead, no target, target dead, self-target, cancelled
        if (_caster.Hp <= 0 || target == null || target.Hp <= 0 || target.ObjId == _caster.ObjId || Cancelled)
        {
            Observe("stopped", target?.ObjId ?? 0);
            StopAutoAttack();
            return;
        }

        // Skill-pause: while another skill is casting or during GCD, skip this tick.
        // We don't cancel — auto-attack will resume on the next tick after skill ends.
        if (_caster.SkillTask != null)
        {
            Observe("casting", target.ObjId);
            return;
        }
        if (_caster.GlobalCooldown >= DateTime.UtcNow)
        {
            Observe("global-cooldown", target.ObjId);
            return;
        }

        if (!_caster.CanAttack(target))
        {
            Observe("cannot-attack", target.ObjId);
            return;
        }

        // Range check — pause (don't cancel) if target moved out of range
        var maxRange = GetEffectiveMaxRange();
        var distance = _caster.GetDistanceTo(target);
        if (distance > maxRange)
        {
            Observe("out-of-range", target.ObjId, distance: distance, maxRange: maxRange);
            return;
        }

        var casterCaster = new SkillCasterUnit(_caster.ObjId);
        var targetCaster = new SkillCastUnitTarget(target.ObjId);
        var skillObject = SkillObject.GetByType(SkillObjectType.None);

        // Fire mainhand attack
        var result = _mainhandSkill.Use(_caster, casterCaster, targetCaster, skillObject, true, out _);
        Observe(result == SkillResult.Success ? "attempted" : "refused", target.ObjId, result, distance, maxRange);

        // Dual-wield: check the current equipment on every tick (not just at task
        // construction). If the player swaps weapons mid-fight, the offhand swing
        // appears / disappears immediately on the next tick.
        var offhandSkill = ResolveOffhandSkill();
        if (offhandSkill != null)
        {
            var offCaster = new SkillCasterUnit(_caster.ObjId);
            var offTarget = new SkillCastUnitTarget(target.ObjId);
            var offSkillObject = SkillObject.GetByType(SkillObjectType.None);
            offhandSkill.Use(_caster, offCaster, offTarget, offSkillObject, true, out _);
        }

        // Adjust delay if attack speed changed (buff/debuff/weapon swap)
        var newDelay = TimeSpan.FromMilliseconds(SkillManager.GetAttackDelay(_mainhandSkill.Template, _caster));
        if (newDelay != RepeatInterval)
            RepeatInterval = newDelay;
    }

    /// <summary>
    /// Decide per tick whether an offhand auto-attack should fire alongside the
    /// mainhand. Returns the offhand Skill if and only if:
    ///   • mainhand task is the melee skill (id 2)
    ///   • offhand currently holds a weapon (anything that's not a shield / empty)
    /// The Skill instance itself is cached lazily on first hit.
    /// </summary>
    private Skill ResolveOffhandSkill()
    {
        if (_mainhandSkill.Template.Id != 2)
            return null;

        var offhandItem = _caster.Equipment?.GetItemBySlot((int)EquipmentItemSlot.Offhand);
        if (offhandItem?.Template is not WeaponTemplate offhandWeapon)
            return null;

        // A shield is also a WeaponTemplate, but it must NOT produce an offhand
        // auto-attack swing. Without this guard a sword+shield loadout swung the
        // shield as if it were a dual-wield weapon (#1454).
        if (offhandWeapon.HoldableTemplate?.SlotTypeId == (uint)EquipmentItemSlotType.Shield)
            return null;

        if (_offhandSkill != null)
            return _offhandSkill;

        _offhandSkillTemplate ??= SkillManager.Instance.GetSkillTemplate(3);
        if (_offhandSkillTemplate == null)
            return null;

        _offhandSkill = new Skill(_offhandSkillTemplate);
        return _offhandSkill;
    }

    private void StopAutoAttack()
    {
        Cancelled = true;
        if (ReferenceEquals(_caster.AutoAttackTask, this))
        {
            _caster.IsAutoAttack = false;
            _caster.AutoAttackTask = null;
        }
        Cancel();
    }

    /// <summary>Get max attack range from equipped weapon or fall back to skill template.</summary>
    private float GetEffectiveMaxRange()
    {
        var slot = _mainhandSkill.Template.Id == 4
            ? EquipmentItemSlot.Ranged
            : EquipmentItemSlot.Mainhand;

        var weapon = _caster.Equipment?.GetItemBySlot((int)slot);
        if (weapon?.Template is WeaponTemplate wt && wt.HoldableTemplate != null && wt.HoldableTemplate.MaxRange > 0)
            return wt.HoldableTemplate.MaxRange;

        return _mainhandSkill.Template.MaxRange > 0 ? _mainhandSkill.Template.MaxRange : 3f;
    }
}
