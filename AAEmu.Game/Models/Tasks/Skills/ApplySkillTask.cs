using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Tasks.Skills;

public class ApplySkillTask(
    Skill skill,
    BaseUnit caster,
    SkillCaster casterCaster,
    BaseUnit target,
    SkillCastTarget targetCaster,
    SkillObject skillObject)
    : Task
{
    public override void Execute()
    {
        if (skill.ExecutionLock != null)
        {
            lock (skill.ExecutionLock)
            {
                if (!skill.CanExecuteObserved(caster))
                    return;
                skill.ApplyEffects(caster, casterCaster, target, targetCaster, skillObject);
                skill.EndSkill(caster);
            }
            return;
        }
        skill.ApplyEffects(caster, casterCaster, target, targetCaster, skillObject);
        skill.EndSkill(caster);
    }
}
