using System.Collections.Concurrent;
using System.Numerics;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.World;

using AAEmu.UnitTests.Game.Core.Managers.Bots;

using Character = AAEmu.Game.Models.Game.Char.Character;
using GameObject = AAEmu.Game.Models.Game.World.GameObject;
using Mate = AAEmu.Game.Models.Game.Units.Mate;

namespace AAEmu.UnitTests.Game.Models.Game.World.Transform;

// NOTE: file-scoped namespace mirrors the source layout; the aliases avoid the UnitTests namespace collisions.

/// <summary>
/// Transform.FinalizeTransform allocation probes (soak follow-up card —
/// per-move heap churn on the shared movement finalize path).
///
/// FinalizeTransform runs for EVERY moving entity (bots AND real players) on
/// EVERY position update. These pins measure it directly, with the transform
/// MOVED between calls (FinalizeTransform early-outs when the world position
/// is unchanged since the last finalize, so a static probe measures nothing):
///
///   - one steady-state MOVE+FINALIZE step through the REAL Transform path
///     for a ROOT (unparented) entity, and for a PARENTED entity (rider under
///     a mate — the mate/ship-rider/doodad-on-slave shape), plus
///   - attribution sub-probes so a regression can be located without
///     re-deriving the breakdown.
///
/// Measurement seam: GC.GetAllocatedBytesForCurrentThread() deltas around a
/// warm, repeated loop (the RegionBroadcastAllocationTests / A2 convention).
/// Budgets are per-operation so machine speed cannot flake them.
///
/// Production shape: the rig session world is REGISTERED in
/// WorldManager._worlds for the measurement window — without registration,
/// every Character.SetPosition → GetWorld logs a FATAL interpolated string
/// (~325B/move) that never happens in production, burying the real signal.
///
/// Measured history (per moved finalize, registered world):
///   - root: 4B/move before AND after the fix — root transforms never
///     cloned (GetWorldPosition returns _localPosRot directly), so the
///     card's "clone per move" premise only bites PARENTED entities.
///   - parented: pre-fix 288B/move (three World accesses per finalize × one
///     PositionAndRotation clone per parent level, ~96B, + SetPosition tail);
///     post-fix 192B/move. The remaining 192B is Character.SetPosition's own
///     tail (its internal Transform.World access + zone/buff work) — outside
///     Transform.cs ownership, deferred.
///
/// With the rig world UNREGISTERED (earlier probe revision), the same path
/// measured 329B/move: every SetPosition → GetWorld logs a FATAL interpolated
/// string. That rig artifact — not Transform allocations — is what the soak
/// card originally attributed to FinalizeTransform.
/// </summary>
[NotInParallel]
public class TransformAllocationTests
{
    private const int WarmupSteps = 1_000;
    private const int MeasuredSteps = 20_000;

    /// <summary>Budget for a root (unparented) entity per moved finalize.</summary>
    private const long MaxBytesPerRootMoveFinalize = 64;

    /// <summary>
    /// Budget for a parented entity per moved finalize. Post-fix steady state
    /// is 192B/move (Character.SetPosition tail); pre-fix was 288B/move.
    /// </summary>
    private const long MaxBytesPerParentedMoveFinalize = 224;

    /// <summary>
    /// Registers a rig session world in WorldManager._worlds for the duration
    /// of <paramref name="body"/> so the probe measures PRODUCTION shape:
    /// without registration, every Character.SetPosition → GetWorld call logs
    /// a FATAL interpolated string (~325B/move) that never happens in prod
    /// (worlds are always registered there). The rig deliberately bypasses
    /// the shared registry for suite-ordering safety; this scoped add/remove
    /// restores just enough production shape to measure the engine path.
    /// Safe under [NotInParallel] (global barrier in TUnit).
    /// </summary>
    private static void WithRegisteredWorld(HeadlessSession session, Action body)
    {
        var worldsField = typeof(WorldManager).GetField("_worlds",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var worlds = (ConcurrentDictionary<uint, WorldInstance>)worldsField.GetValue(WorldManager.Instance)!;
        worlds.TryAdd(session.World.Id, session.World);
        try
        {
            body();
        }
        finally
        {
            worlds.TryRemove(session.World.Id, out _);
        }
    }

    /// <summary>
    /// Mirrors the rig's SummonSlave instance-id bypass for a rig-summoned
    /// mate: SummonMate never wires Transform._instanceId /
    /// GameObject._parentWorld backing fields, so parenting a rider to the
    /// mate makes GetRegion resolve through the MATE and NRE headless.
    /// </summary>
    private static void WireMateToWorld(HeadlessSession session, Mate mate)
    {
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(mate.Transform, session.World.Id);
        typeof(GameObject)
            .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(mate, session.World);
    }

    private static (Character Character, HeadlessSession Session) CreateCharacter(string name)
    {
        // No config JSON is loaded in unit tests: AppConfiguration.Instance.World
        // is null by default (GameplayActorTestRig convention) — the real
        // SetPosition/Finalize path needs World.MOTD headless.
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, session) = GameplayActorTestRig.CreateActor(name);
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        actor.Character.Transform.FinalizeTransform(); // establish _lastFinalizePos
        return (actor.Character, session);
    }

    /// <summary>Moves the character's local position by one small walk-step delta.</summary>
    private static void MoveStep(Character character)
    {
        var p = character.Transform.Local.Position;
        character.Transform.Local.Position = new Vector3(p.X + 0.05f, p.Y + 0.05f, p.Z);
    }

    [Test]
    public async Task FinalizeTransform_MovingRootEntity_BoundedAllocationPerMove()
    {
        var (character, session) = CreateCharacter("alloc-finalize-root");

        var allocated = 0L;
        WithRegisteredWorld(session, () =>
        {
            // Warm up: JIT tiering + SusManager delta-analysis window (fires
            // once at first finalize, then every ~5s real time — warmup
            // absorbs both).
            for (var i = 0; i < WarmupSteps; i++)
            {
                MoveStep(character);
                character.Transform.FinalizeTransform();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < MeasuredSteps; i++)
            {
                MoveStep(character);
                character.Transform.FinalizeTransform();
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        var perMove = allocated / MeasuredSteps;
        await Assert.That(perMove < MaxBytesPerRootMoveFinalize)
            .IsTrue()
            .Because($"a moved root-entity FinalizeTransform must stay under {MaxBytesPerRootMoveFinalize}B/move " +
                     $"(soak follow-up card: per-move heap churn shared by bots and real players); saw {perMove}B/move");
    }

    [Test]
    public async Task FinalizeTransform_MovingParentedEntity_BoundedAllocationPerMove()
    {
        // Parented case (mates, riders, doodads on slaves): the pre-fix path
        // cloned a PositionAndRotation per parent level on EVERY World access
        // — three per finalize (delta check, AddVisibleObject region lookup,
        // reset). This pin keeps the parented per-move cost bounded too.
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (riderActor, riderSession) = GameplayActorTestRig.CreateActor("alloc-finalize-parented");
        var rider = riderActor.Character;
        GameplayActorTestRig.SummonMate(riderSession, riderActor);
        var mate = riderSession.World.MateManager.GetActiveMates(rider.Id)[0];
        WireMateToWorld(riderSession, mate);
        rider.Transform.Parent = mate.Transform;
        rider.Transform.FinalizeTransform(); // establish _lastFinalizePos

        var allocated = 0L;
        WithRegisteredWorld(riderSession, () =>
        {
            for (var i = 0; i < WarmupSteps; i++)
            {
                MoveStep(rider);
                rider.Transform.FinalizeTransform();
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < MeasuredSteps; i++)
            {
                MoveStep(rider);
                rider.Transform.FinalizeTransform();
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        var perMove = allocated / MeasuredSteps;
        await Assert.That(perMove < MaxBytesPerParentedMoveFinalize)
            .IsTrue()
            .Because($"a moved PARENTED-entity FinalizeTransform must stay under {MaxBytesPerParentedMoveFinalize}B/move " +
                     "(pre-fix: one PositionAndRotation clone per parent level per World access); saw " + perMove + "B/move");
    }

#if !MEASURE_BASELINE
    [Test]
    public async Task Attribution_ComputeWorldPosition_MatchesWorldClonePosition()
    {
        // Correctness guard for the allocation-free composer used by the hot
        // path: must agree with the reference World.ClonePosition()/World
        // values on both root AND parented transforms.
        AppConfiguration.Instance.World ??= new WorldConfig();

        var (_, rootSession) = GameplayActorTestRig.CreateActor("attrib-compose-root");
        var root = rootSession.Character;

        var (riderActor2, riderSession2) = GameplayActorTestRig.CreateActor("attrib-compose-rider");
        var rider2 = riderActor2.Character;
        GameplayActorTestRig.SummonMate(riderSession2, riderActor2);
        var mate2 = riderSession2.World.MateManager.GetActiveMates(rider2.Id)[0];
        WireMateToWorld(riderSession2, mate2);

        var rootMaxDelta = 0f;
        WithRegisteredWorld(rootSession, () =>
        {
            for (var i = 1; i <= 100; i++)
            {
                root.Transform.Local.SetPosition(i * 0.5f, i * 0.25f, i * 0.125f);
                var computed = root.Transform.ComputeWorldPosition();
                var reference = root.Transform.World.Position;
                rootMaxDelta = MathF.Max(rootMaxDelta,
                    MathF.Max(MathF.Abs(computed.X - reference.X),
                        MathF.Max(MathF.Abs(computed.Y - reference.Y), MathF.Abs(computed.Z - reference.Z))));
            }
        });

        // Parented case: real parent chain (rider transform under a mate).
        rider2.Transform.Parent = mate2.Transform;
        var riderMaxDelta = 0f;
        for (var i = 1; i <= 100; i++)
        {
            mate2.Transform.Local.SetPosition(i * 0.5f, -i * 0.25f, 3f);
            rider2.Transform.Local.Position = new Vector3(0.5f, 0.5f, 0.5f);
            var computed = rider2.Transform.ComputeWorldPosition();
            var reference = rider2.Transform.World.Position;
            riderMaxDelta = MathF.Max(riderMaxDelta,
                MathF.Max(MathF.Abs(computed.X - reference.X),
                    MathF.Max(MathF.Abs(computed.Y - reference.Y), MathF.Abs(computed.Z - reference.Z))));
        }

        await Assert.That(rootMaxDelta < 0.0001f)
            .IsTrue().Because($"root-transform composition diverged from World.ClonePosition: {rootMaxDelta}");
        await Assert.That(riderMaxDelta < 0.001f)
            .IsTrue().Because($"parented-transform composition diverged from World.ClonePosition: {riderMaxDelta}");
    }
#endif

    [Test]
    public async Task Attribution_WorldAccess_Root_AllocationFree()
    {
        var (character, session) = CreateCharacter("attrib-worldaccess");

        var allocated = 0L;
        WithRegisteredWorld(session, () =>
        {
            for (var i = 0; i < WarmupSteps; i++)
                _ = character.Transform.World.ClonePosition();

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < MeasuredSteps; i++)
                _ = character.Transform.World.ClonePosition();
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        await Assert.That(allocated < MaxBytesPerRootMoveFinalize)
            .IsTrue()
            .Because("a World access on a ROOT transform composes no parent chain " +
                     "and must not allocate; saw " + allocated + " bytes");
    }

    [Test]
    public async Task Attribution_AddVisibleObject_UnchangedRegion_AllocationFree()
    {
        var (character, session) = CreateCharacter("attrib-addvis");

        var allocated = 0L;
        WithRegisteredWorld(session, () =>
        {
            for (var i = 0; i < WarmupSteps; i++)
            {
                MoveStep(character);
                WorldManager.Instance.AddVisibleObject(character);
            }

            var before = GC.GetAllocatedBytesForCurrentThread();
            for (var i = 0; i < MeasuredSteps; i++)
            {
                MoveStep(character);
                WorldManager.Instance.AddVisibleObject(character);
            }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        });

        var perCall = allocated / MeasuredSteps;
        await Assert.That(perCall < MaxBytesPerRootMoveFinalize)
            .IsTrue()
            .Because("AddVisibleObject with an UNCHANGED region must early-out allocation-free " +
                     "(steady-state per-move path); saw " + perCall + "B/call");
    }
}
