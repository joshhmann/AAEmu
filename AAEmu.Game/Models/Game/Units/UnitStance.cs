namespace AAEmu.Game.Models.Game.Units;

/// <summary>
/// Physical posture stance for units (standing, sitting).
/// Controls sitting regen multiplier for out-of-combat recovery.
/// </summary>
public enum UnitStance : byte
{
    Stand = 0,
    Sit = 1
}
