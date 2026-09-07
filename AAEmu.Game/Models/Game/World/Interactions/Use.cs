using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units;

using NLog;

namespace AAEmu.Game.Models.Game.World.Interactions;

public class Use : IWorldInteraction
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    public void Execute(BaseUnit caster, SkillCaster casterType, BaseUnit target, SkillCastTarget targetType,
        uint skillId, uint doodadId, DoodadFuncTemplate objectFunc = null)
    {
        Logger.Debug($"World interaction SkillID: {skillId}");
        if (target is Doodad doodad)
        {
            // Cargo loading support: if target doodad is a pack storage box on a vehicle/slave,
            // and character is carrying a trade pack in their backpack equipment slot, load it onto the vehicle!
            if (caster is Character player && doodad.ParentObj is Slave slave && PackVehicleService.IsPackStorageBoxDoodad(doodad.TemplateId))
            {
                var loadResult = PackVehicleService.TryLoadCarriedPack(player, slave, out _);
                if (loadResult == PackVehicleService.PackLoadResult.Success)
                {
                    Logger.Debug($"Loaded carried pack onto slave {slave.ObjId} (doodad {doodad.ObjId})");
                    return;
                }
            }

            doodad.Use(caster, skillId);
            return;
        }

        // Direct vehicle interaction: if player interacted with the vehicle itself while carrying a trade pack,
        // and vehicle has available cargo points, load it onto the vehicle!
        if (target is Slave directSlave && caster is Character slaveCarrier)
        {
            if (PackVehicleService.GetCargoPoints(directSlave).Count > 0)
            {
                var loadResult = PackVehicleService.TryLoadCarriedPack(slaveCarrier, directSlave, out _);
                if (loadResult == PackVehicleService.PackLoadResult.Success)
                {
                    Logger.Debug($"Loaded carried pack onto slave {directSlave.ObjId} directly");
                    return;
                }
            }
        }

        // Fallback: some gather interactions (e.g. wells / "Draw Water" skill 13154) are cast
        // self-targeted by the client, so the doodad is never the skill's target and doodad.Use()
        // never runs (= no water). Find the nearest doodad around the caster whose CURRENT phase has
        // a use-func matching this skill, and use it -> drives the intact DoodadFuncUse -> next phase
        // -> DoodadFuncLootItem chain. Only doodads that actually respond to this skill match, so it
        // does not disturb other gathers (those still hit the target-is-Doodad branch above).
        if (caster is Character)
        {
            var nearby = WorldManager.GetAround<Doodad>(caster, 6f);
            if (nearby != null && nearby.Count > 0)
            {
                Doodad best = null;
                var bestDistSq = float.MaxValue;
                var cp = caster.Transform.World.Position;
                foreach (var d in nearby)
                {
                    if (DoodadManager.Instance.GetFunc(d.FuncGroupId, skillId) == null)
                        continue;
                    var dp = d.Transform.World.Position;
                    var distSq = (dp.X - cp.X) * (dp.X - cp.X) + (dp.Y - cp.Y) * (dp.Y - cp.Y) + (dp.Z - cp.Z) * (dp.Z - cp.Z);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = d;
                    }
                }

                if (best != null)
                {
                    Logger.Debug($"Use fallback: skill {skillId} -> nearby doodad objId={best.ObjId} template={best.TemplateId} phase={best.FuncGroupId}");
                    best.Use(caster, skillId);
                }
            }
        }

        /*
        // TODO ID=21902, View Fish Finder: Scan around with the Fish Finder. Detected schools of fish will be displayed on the map.
        if (skillId == SkillsEnum.ViewFishFinder)
        {
            var characterTarget = target as Character;
            var doodads = WorldManager.GetAround<Doodad>(caster, 1f);
            if ((doodads != null) && (characterTarget != null))
            {
                foreach (var d in doodads)
                {
                    FishSchoolManager.FishFinderStart(characterTarget);
                }
            }
        }
        */
    }
}
