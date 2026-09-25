namespace AAEmu.Game.Models.Game.Quests.Director;

/// <summary>
/// How proven one actor verb is: <see cref="Frozen"/> (a closed gate froze the
/// behavior), <see cref="Fresh"/> (a live gate proved it against the current
/// engine), <see cref="Open"/> (no gate owns it yet, so it is UNPROVEN and a
/// plan that needs it must fail closed).
/// </summary>
public enum VerbGateStatus : byte
{
    Open = 0,
    Frozen = 1,
    Fresh = 2
}
