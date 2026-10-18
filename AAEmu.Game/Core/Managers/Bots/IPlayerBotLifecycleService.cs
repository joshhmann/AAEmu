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
    /// Retires existing native work for the exact Deactivating owner and
    /// generation on the game-loop boundary, without activation or physical
    /// leave/save. The manager releases the entry lock for this finite handoff.
    /// False (including abandonment) leaves admission closed and forbids save;
    /// absence is success only after exact owner identity has been checked.
    /// </summary>
    bool TryRetireNativeWork(PlayerBotRuntime expectedOwner, ulong expectedGeneration, string reason);

    /// <summary>
    /// Performs physical leave/save after acknowledged exact native retirement.
    /// Returns false when refused or failed — the manager retains the owner
    /// as DeactivationFailed, never restores Active, and does not retry.
    /// </summary>
    bool Deactivate(Character character, string reason);
}
