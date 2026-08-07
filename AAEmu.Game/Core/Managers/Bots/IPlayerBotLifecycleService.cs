using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Lifecycle seam for player bots (additive, slice #5 of the PlayerBot scale
/// review — ARCHITECTURE_REVIEW deliverable 10).
///
/// The registry (IPlayerBotManager) delegates embodiment/teardown to a
/// lifecycle service instead of performing it inline, so the manager stays a
/// pure registry and the actual character activation lives in one place.
///
/// Wiring note: slice #3 (CharacterLifecycleService extraction) defines
/// <c>ICharacterLifecycleService</c> with ActivateHuman/ActivateHeadless/
/// Deactivate. When that card merges, its implementation should also
/// implement this seam (or Program.cs should register an adapter) so bots
/// activate through the same Load → ObjId → TryAddCharacter → Simulation →
/// buffs/HP/MP path the human path uses. Until then the DI default
/// (<see cref="UnwiredPlayerBotLifecycleService"/>) fails closed: activation
/// is refused and counted in diagnostics rather than silently succeeding
/// without a world entry.
/// </summary>
public interface IPlayerBotLifecycleService
{
    /// <summary>
    /// Embodies a registered bot character into the world (headless: no
    /// client packets). Returns false when refused or failed — the manager
    /// keeps the bot non-active and records a failed activation.
    /// </summary>
    bool ActivateHeadless(Character character, object? botContext);

    /// <summary>
    /// Tears down an embodied bot character. Returns false when refused or
    /// failed — the manager keeps the bot active and records a failed
    /// deactivation.
    /// </summary>
    bool Deactivate(Character character, string reason);
}
