namespace AAEmu.Game.Models.Game.Quests.Director;

/// <summary>
/// The verified-actor-verb lookup a quest plan gates on: one verb key in, its
/// <see cref="VerbGate"/> out. A key no gate owns resolves to
/// <see cref="VerbGate.Absent"/> (OPEN), so callers never need a second
/// missing-key path — "absent" and "open" are the same fail-closed answer.
///
/// Production reads the static table (<see cref="VerifiedVerbRegistry"/>) and
/// never mutates; a test that must stage an unproven verb passes its own
/// implementation to <c>QuestDirector.Plan</c> instead of reaching into a
/// global.
/// </summary>
public interface IVerifiedVerbRegistry
{
    /// <summary>
    /// The gate for <paramref name="verbKey"/>: FROZEN/FRESH with the gate that
    /// proved it, or OPEN (no gate id, no evidence) when no gate owns the key.
    /// </summary>
    VerbGate Resolve(string verbKey);
}
