using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Transition safety gate for fidelity downgrades (spec §11 verbatim):
/// a bot may NEVER be downgraded while it is in combat / attached to a Slave /
/// carrying a trade pack / in trial / grouped with a human / saving.
///
/// The probe is a seam so the rig can simulate each condition deterministically;
/// <see cref="BotTransitionSafetyProbe"/> is the production default reading
/// live Character state.
/// </summary>
public interface IBotTransitionSafetyProbe
{
    /// <summary>True when the character is in battle (Unit.IsInBattle).</summary>
    bool IsInCombat(Character character);

    /// <summary>True when the character is attached to (riding) a Slave.</summary>
    bool IsAttachedToSlave(Character character);

    /// <summary>True when the character is carrying a trade pack (Backpack slot).</summary>
    bool IsCarryingTradePack(Character character);

    /// <summary>True when the character is in trial (ForciblyAwaitingTrial buff).</summary>
    bool IsInTrial(Character character);

    /// <summary>True when the character is grouped with a human (party).</summary>
    bool IsGroupedWithHuman(Character character);

    /// <summary>True when the character is mid-save.</summary>
    bool IsSaving(Character character);
}
