using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Units;

namespace AAEmu.Game.Models.Game.Skills.Effects.SpecialEffects;

public class DeclareDominion : SpecialEffectAction
{
    protected override SpecialType SpecialEffectActionType => SpecialType.DeclareDominion;

    public override void Execute(BaseUnit caster,
        SkillCaster casterObj,
        BaseUnit target,
        SkillCastTarget targetObj,
        CastAction castObj,
        Skill skill,
        SkillObject skillObject,
        DateTime time,
        int value1,
        int value2,
        int value3,
        int value4)
    {
        if (caster is Character) { Logger.Debug("Special effects: DeclareDominion value1 {0}, value2 {1}, value3 {2}, value4 {3}", value1, value2, value3, value4); }

        if (((Unit)caster).Expedition == null)
            return;

        // Check target is not already claimed
        if (target is not House lodestone)
            return;

        // Get target zone, radius, etc..

        // Advance building step on target

        // Create new dominion data (canonical blob shape lives in DominionManager),
        // persist it in the MySQL dominions table and broadcast server-wide.
        // Slice-2 will replace the remaining seed values with real zone data,
        // monument targeting and declare-window/permission checks.
        var expedition = ((Unit)caster).Expedition;
        var position = lodestone.Transform.World.Position;
        var dominion = DominionManager.BuildDominionData(
            ZoneManager.Instance.GetZoneByKey(lodestone.Transform.ZoneId).GroupId,
            (uint)expedition.Id,
            lodestone.Id,
            position.X, position.Y, position.Z,
            50,
            DateTime.UtcNow);
        DominionManager.Instance.Declare(dominion, expedition.Name);
        if (caster is Character character)
        {
            // character.Inventory.Equipment.
            var backpack = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            character.Inventory.Equipment.ConsumeItem(ItemTaskType.SkillReagents, backpack.TemplateId, 1, backpack);
        }
    }
}
