using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Siege;
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

        if (caster is not Unit unit || unit.Expedition == null)
            return;

        // Check target is not already claimed
        if (target is not House lodestone)
            return;

        // Slice-2 gates: the zone must ship siege data, the caster's expedition
        // role must carry dominion_declare, the zone must be inside its declare
        // window, and the zone's declare pack must be equipped (fail-closed when
        // the zone ships a non-zero DeclareItemId).
        var zoneGroupId = ZoneManager.Instance.GetZoneByKey(lodestone.Transform.ZoneId).GroupId;
        var manager = DominionManager.Instance;
        var zone = manager.GetSiegeZoneByGroup(zoneGroupId);

        uint? expeditionId = (uint)unit.Expedition.Id;
        var hasPolicy = false;
        if (caster is Character policyCharacter)
        {
            var member = unit.Expedition.GetMember(policyCharacter);
            hasPolicy = member != null && unit.Expedition.GetPolicyByRole(member.Role)?.DominionDeclare == true;
        }

        uint equippedBackpackTemplateId = 0;
        if (caster is Character declareCharacter)
            equippedBackpackTemplateId = declareCharacter.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack)?.TemplateId ?? 0;

        var phase = zone == null ? SiegePhase.Peace : manager.GetCurrentPhase(zone, DateTime.UtcNow);
        var refusal = DominionManager.ValidateDeclare(zone, expeditionId, hasPolicy, phase, equippedBackpackTemplateId);
        if (refusal != null)
        {
            Logger.Debug("Special effects: DeclareDominion refused {0} (zone_group {1}, expedition {2})",
                refusal, zoneGroupId, expeditionId);
            return;
        }

        // Create new dominion data (canonical blob shape lives in DominionManager),
        // persist it in the MySQL dominions table and broadcast server-wide.
        var expedition = unit.Expedition;
        var position = lodestone.Transform.World.Position;
        var dominion = DominionManager.BuildDominionData(
            zoneGroupId,
            expeditionId.Value,
            lodestone.Id,
            position.X, position.Y, position.Z,
            50,
            DateTime.UtcNow);
        manager.Declare(dominion, expedition.Name);
        // Trimmed-DB case: a zero DeclareItemId means the pack gate was skipped,
        // so there is nothing to consume (zone null is defensive; validator already refused it).
        if (caster is Character character && zone != null && zone.DeclareItemId != 0)
        {
            // character.Inventory.Equipment.
            var backpack = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
            if (backpack == null)
            {
                Logger.Warn("Special effects: DeclareDominion has no backpack equipped to consume for {0}", character.Name);
                return;
            }
            character.Inventory.Equipment.ConsumeItem(ItemTaskType.SkillReagents, backpack.TemplateId, 1, backpack);
        }
    }
}
