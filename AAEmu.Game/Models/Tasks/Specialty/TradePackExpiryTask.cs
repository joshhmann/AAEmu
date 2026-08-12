using AAEmu.Game.Core.Managers.World;

namespace AAEmu.Game.Models.Tasks.Specialty;

/// <summary>
/// Periodic sweep for placed trade packs past their canonical 6-day despawn time.
/// Canonical 1.2: "내려놓은 등짐은 6일 후 소멸" (M4-2, t_449d0c41).
/// </summary>
public class TradePackExpiryTask : Task
{
    public override void Execute()
    {
        SpecialtyManager.Instance.SweepExpiredPlacedPacks();
    }
}
