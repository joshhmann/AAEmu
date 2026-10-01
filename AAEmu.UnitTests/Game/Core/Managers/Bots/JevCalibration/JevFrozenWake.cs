#nullable enable

using AAEmu.Game.Core.Managers.Bots.Combat;
using AAEmu.Game.Core.Managers.Bots.Needs;
using AAEmu.Game.Core.Managers.Bots.Survival;
using AAEmu.Game.Core.Managers.Bots.Travel;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots.JevCalibration;

/// <summary>
/// Which brain a frozen wake belongs to. One member per brain with a pure
/// <c>Decide</c> surface; the replay harness dispatches on it.
/// </summary>
public enum JevBrain
{
    Travel = 0,
    Survival = 1,
    Needs = 2,
    Combat = 3,
}

/// <summary>
/// A frozen travel wake: the inputs exactly as recorded plus the verdict the
/// live wake reached. The replay harness re-runs <see cref="TravelBrain.Decide"/>
/// over <see cref="Inputs"/> and asserts the verdict still equals
/// <see cref="Expected"/>.
/// </summary>
public readonly record struct JevTravelWake(string WakeId, TravelBrainInputs Inputs, TravelDecision Expected);

/// <summary>
/// A frozen survival wake: the inputs exactly as recorded plus the verdict the
/// live wake reached (see <see cref="JevTravelWake"/> for the replay contract).
/// </summary>
public readonly record struct JevSurvivalWake(string WakeId, SurvivalBrainInputs Inputs, SurvivalBrainDecision Expected);

/// <summary>
/// A frozen needs wake: the inputs exactly as recorded plus the verdict the
/// live wake reached (see <see cref="JevTravelWake"/> for the replay contract).
/// </summary>
public readonly record struct JevNeedsWake(string WakeId, NeedsBrainInputs Inputs, NeedsBrainDecision Expected);

/// <summary>
/// A frozen combat wake: the inputs exactly as recorded (including the
/// candidate rows and the frozen <c>NowUtc</c> stamp) plus the verdict the live
/// wake reached (see <see cref="JevTravelWake"/> for the replay contract).
///
/// Note: <see cref="CombatBrain.Decide"/> reads only the frozen commitment
/// facts (<c>CommitmentInForce</c>, <c>IncumbentScore</c>) — never the live
/// engagement table and never the clock — so the frozen <c>NowUtc</c> is
/// carried for provenance, not re-read.
/// </summary>
public readonly record struct JevCombatWake(string WakeId, CombatBrainInputs Inputs, CombatBrainDecision Expected);
