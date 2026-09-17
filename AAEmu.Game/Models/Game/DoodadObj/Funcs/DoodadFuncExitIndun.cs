﻿using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Utils;

namespace AAEmu.Game.Models.Game.DoodadObj.Funcs;

public class DoodadFuncExitIndun : DoodadFuncTemplate
{
    // doodad_funcs
    public uint ReturnPointId { get; set; }

    public override void Use(BaseUnit caster, Doodad owner, uint skillId, int nextPhase = 0)
    {
        Logger.Info("DoodadFuncExitIndun, ReturnPointId: {0}", ReturnPointId);

        if (caster is Character character)
        {
            // GM-teleport entrants never recorded a MainWorldPosition (the enter
            // funcs only stamp it on a normal gate entry). Fall back to the
            // faction racial return so the exit door still has a return target.
            if (ReturnPointId == 0 && character.MainWorldPosition == null && !TrySetFactionReturnFallback(character))
            {
                // TODO in db not have a entries, but we can change this xD
                Logger.Info("DoodadFuncExitIndun, Not have return point!");
                character.SendErrorMessage(ErrorMessageType.InvalidReturnPosInstance); // ошибка, не можете выйти сейчас из данжона
                return;
            }

            if (ReturnPointId == 0 && character.MainWorldPosition != null)
            {
                IndunManager.Instance.RequestLeaveInstance(character);
            }
            else
            {
                // TODO in db not have a entries, but we can change this xD
                Logger.Info("DoodadFuncExitIndun, Not have return point!");
                character.SendErrorMessage(ErrorMessageType.InvalidReturnPosInstance); // ошибка, не можете выйти сейчас из данжона
                //character.SendErrorMessage(ErrorMessageType.TryLaterInstance); // ошибка данжона, пробуй еще раз
                //character.SendErrorMessage(ErrorMessageType.InvalidStateInstance); // данжон уже загружен
                //character.SendErrorMessage(ErrorMessageType.ProhibitedInInstance); // нельзя это сделать внутри данжона
                //character.SendErrorMessage(ErrorMessageType.InstanceVisitLimit); // Ты израсходовал лимит на вход в данжон. Пробуй позже.
            }
        }
    }

    /// <summary>
    /// Resolves the character's faction racial return point — the same
    /// GetDistrictReturnPoint/GetRecallById path the Return skill uses — and
    /// records it as the MainWorldPosition so RequestLeaveInstance has a
    /// return target. Returns false when no fallback resolves.
    /// </summary>
    internal static bool TrySetFactionReturnFallback(Character character)
    {
        var returnPointId = PortalManager.Instance.GetDistrictReturnPoint(character.ReturnDistrictId, character.Faction.Id);
        var recall = returnPointId == 0 ? null : PortalManager.Instance.GetRecallById(returnPointId);
        if (recall == null)
            return false;
        character.MainWorldPosition = new Transform(
            character, null, recall.ZoneId, 0, recall.X, recall.Y, recall.Z, recall.Yaw.DegToRad());
        return true;
    }
}
