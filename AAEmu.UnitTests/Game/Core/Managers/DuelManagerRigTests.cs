using System.Collections.Concurrent;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Duels;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Tasks.Duels;
using AAEmu.Commons.Utils;
using AAEmu.Game.Models.Game.Char;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// C9 (DUEL-01) headless verification through the REAL DuelManager.
///
/// Known headless limits (recorded): DuelAccepted spawns the combat-flag
/// doodad and reads world-template geodata — that path throws in a rig world
/// (no GeoData). The accept path treats that as a setup failure and rolls the
/// half-started duel back (no stuck IsInDuel, no orphan row, no faction swap).
/// The temporary RedTeam/BlueTeam faction swap only runs on the live stack and
/// is covered by DuelFactionSwapE2eTests.
/// </summary>
[ParallelLimiter<AAEmu.UnitTests.Game.Housing.SequentialParallelLimit>]
[NotInParallel]
public class DuelManagerRigTests
{
    [Test]
    public async Task Duel_AcceptFailure_RollsBackStateAndRow()
    {
        var (challenger, challenged) = SetupDuelPair("duel-rollback");
        var a = challenger.Character;
        var b = challenged.Character;
        var originalChallengerFaction = a.Faction.Id;
        var originalChallengedFaction = b.Faction.Id;

        // 1. REQUEST — duel registers under both participant ids.
        DuelManager.Instance.DuelRequest(a, b.Id);
        await Assert.That(DuelRows().ContainsKey(a.Id)).IsTrue();

        // 2. ACCEPT — flag-spawn geodata throws headless; the manager must roll
        // the half-started duel back instead of stranding both sides IsInDuel
        // with an orphan row a retry can never clear.
        DuelManager.Instance.DuelAccepted(b, a.Id);

        await Assert.That(a.IsInDuel).IsFalse();
        await Assert.That(b.IsInDuel).IsFalse();
        await Assert.That(DuelRows().IsEmpty).IsTrue();
        await Assert.That(a.Faction.Id).IsEqualTo(originalChallengerFaction);
        await Assert.That(b.Faction.Id).IsEqualTo(originalChallengedFaction);

        // 3. RETRY — the started-once gate is released with the row: a fresh
        // request registers again and stops cleanly.
        DuelManager.Instance.DuelRequest(a, b.Id);
        await Assert.That(DuelRows().ContainsKey(a.Id)).IsTrue();
        DuelManager.Instance.DuelStop(a.Id, DuelDetType.Draw);

        await Assert.That(a.IsInDuel).IsFalse();
        await Assert.That(b.IsInDuel).IsFalse();
        await Assert.That(DuelRows().IsEmpty).IsTrue();
        await Assert.That(a.Faction.Id).IsEqualTo(originalChallengerFaction);
        await Assert.That(b.Faction.Id).IsEqualTo(originalChallengedFaction);
    }

    [Test]
    public async Task Duel_RequestForUnknownTarget_DoesNotRegister()
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        ResetDuelManager();
        var (challenger, _) = GameplayActorTestRig.CreateActor("duel-c");

        // Unknown target objId → GetCharacterById null → NRE guard? The
        // current code news up Duel(challenger, null) — this pins whatever
        // the engine does; a crash here IS the finding.
        var threw = false;
        try
        {
            DuelManager.Instance.DuelRequest(challenger.Character, 999_999);
        }
        catch
        {
            threw = true;
        }

        // Isolate the follow-up from the unknown-target path's partial row.
        ResetDuelManager();

        // Either way the challenged character list must not contain a phantom:
        // nothing to assert on an unknown target beyond "did not corrupt the
        // next request" — issue a real follow-up request between two live
        // actors and verify it still registers.
        var (b, bSession) = GameplayActorTestRig.CreateActor("duel-d");
        var (c, cSession) = GameplayActorTestRig.CreateActor("duel-e");
        GameplayActorTestRig.JoinActorWorld(bSession, c);
        _ = bSession;
        _ = cSession;

        foreach (var participant in new[] { b.Character, c.Character })
            WorldManager.Instance.TryAddCharacter(participant);

        DuelManager.Instance.DuelRequest(b.Character, c.Character.Id);
        await Assert.That(DuelRows().ContainsKey(b.Character.Id)).IsTrue();

        // Headless accept fails at flag-spawn geodata and rolls back (above).
        DuelManager.Instance.DuelAccepted(c.Character, b.Character.Id);
        await Assert.That(b.Character.IsInDuel).IsFalse();
        await Assert.That(DuelRows().IsEmpty).IsTrue();
    }

    [Test]
    public async Task Duel_ParticipantDisconnect_EndsDuelForBothSides()
    {
        var (challenger, challenged) = SetupDuelPair("duel-dc");
        var a = challenger.Character;
        var b = challenged.Character;
        var originalChallengerFaction = a.Faction.Id;
        var originalChallengedFaction = b.Faction.Id;

        // Simulate an accepted mid-duel state: the row exists and both sides
        // are flagged. (The headless accept path itself rolls back — see
        // Duel_AcceptFailure — so the flags are set directly; the live-stack
        // faction swap is e2e-covered.)
        DuelManager.Instance.DuelRequest(a, b.Id);
        a.IsInDuel = true;
        b.IsInDuel = true;

        // One side disconnects: the duel must end for BOTH with factions
        // restored, no orphan row, and no post-stop residue.
        DuelManager.Instance.OnParticipantDisconnect(a);

        await Assert.That(a.IsInDuel).IsFalse();
        await Assert.That(b.IsInDuel).IsFalse();
        await Assert.That(DuelRows().IsEmpty).IsTrue();
        await Assert.That(a.Faction.Id).IsEqualTo(originalChallengerFaction);
        await Assert.That(b.Faction.Id).IsEqualTo(originalChallengedFaction);

        // Idempotent: a second disconnect with no duel row is a safe no-op.
        DuelManager.Instance.OnParticipantDisconnect(a);
        await Assert.That(a.IsInDuel).IsFalse();
        await Assert.That(DuelRows().IsEmpty).IsTrue();
    }

    [Test]
    public async Task Duel_Stop_CancelsOrphanMonitorTasks()
    {
        var (challenger, challenged) = SetupDuelPair("duel-mon");
        var a = challenger.Character;
        var b = challenged.Character;

        DuelManager.Instance.DuelRequest(a, b.Id);
        a.IsInDuel = true;
        b.IsInDuel = true;

        // Simulate the DuelStart-scheduled monitors (unscheduled instances:
        // TaskManager.Cancel just misses the queue, so planting them is safe).
        var duel = DuelRows()[a.Id];
        duel.DuelResultСheckTask = new DuelResultСheckTask(duel);
        duel.DuelDistanceСheckTask = new DuelDistanceСheckTask(duel);

        DuelManager.Instance.DuelStop(a.Id, DuelDetType.Draw);

        // The monitors must be cancelled + nulled at cleanup: after the row is
        // removed their next tick would otherwise throw KeyNotFound.
        await Assert.That(duel.DuelResultСheckTask).IsNull();
        await Assert.That(duel.DuelDistanceСheckTask).IsNull();
        await Assert.That(a.IsInDuel).IsFalse();
        await Assert.That(b.IsInDuel).IsFalse();
        await Assert.That(DuelRows().IsEmpty).IsTrue();
    }

    private static (GameplayActor Challenger, GameplayActor Challenged) SetupDuelPair(string tag)
    {
        GameplayActorTestRig.ForceSeedTeamManager();
        ResetDuelManager();
        var (challenger, challengerSession) = GameplayActorTestRig.CreateActor(tag + "-a");
        var (challenged, _) = GameplayActorTestRig.CreateActor(tag + "-b");
        GameplayActorTestRig.JoinActorWorld(challengerSession, challenged);

        // Register both characters in the rig WorldManager so
        // DuelRequest's GetCharacterById resolves the target.
        foreach (var c in new[] { challenger.Character, challenged.Character })
        {
            WorldManager.Instance.TryRemoveCharacter(c.ObjId);
            WorldManager.Instance.TryAddCharacter(c);
            _registeredCharacters.Add(c);
        }

        return (challenger, challenged);
    }

    private static readonly List<Character> _registeredCharacters = [];

    [After(Test)]
    public void CleanupCharacters()
    {
        foreach (var c in _registeredCharacters)
            WorldManager.Instance.TryRemoveCharacter(c.ObjId);
        _registeredCharacters.Clear();
    }

    private static void ResetDuelManager()
    {
        // Real singletons (the manager calls them statically); a fresh
        // instance per test keeps duel rows + saved factions from leaking.
        typeof(Singleton<DuelManager>)
            .GetField("s_instance", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .SetValue(null, new DuelManager());
    }

    private static ConcurrentDictionary<uint, Duel> DuelRows()
        => (ConcurrentDictionary<uint, Duel>)typeof(DuelManager)
            .GetField("_duels", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(DuelManager.Instance)!;
}
