using System.Net;
using System.Net.Sockets;
using System.Numerics;

using AAEmu.Commons.Network;
using AAEmu.Commons.Network.Core;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Bots;

using WorldConfig = AAEmu.Game.Models.Game.WorldConfig;

using Microsoft.Extensions.Time.Testing;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// G3-B5 leakage-audit negative tests (ROADMAP G3-B5 / development-loop
/// rule 5): each test-only seam must be PROVABLY unreachable from
/// player-visible sessions and never feed autonomous bot decisions.
///
/// One test group per audited seam:
///  - <see cref="B5BroadcastMovementOptOutLeakageTests"/> — the headless-roam
///    BroadcastMovement opt-out (615a645c9) must be confined to
///    <see cref="BotRoamStepExecutor"/>'s own step loop, and even while it is
///    active the observer-visible movement stream (SCOneUnitMovementPacket)
///    must keep flowing through the executor's throttled broadcast.
///  - <see cref="B5BridgeMetricsLeakageTests"/> — the E2E bridge metrics
///    surface (<see cref="BotDriveBridge"/> CollectGateMetrics et al.) must
///    stay dark under player-session configuration and expose no game-path
///    entry point beyond TryStart.
///  - <see cref="B5RigSeedHookIsolationTests"/> — rig seed hooks
///    (GameplayActorTestRig.Seed* template/singleton mutations) must live
///    only in test assemblies; no shipping assembly may reference them.
/// </summary>

/// <summary>
/// Seam: GameplayActor.BroadcastMovement opt-out. Writers allowed: ONLY
/// BotRoamStepExecutor.StepAsync (headless roam). Readers: ONLY
/// GameplayActor.ApplyCharacterMove (per-apply packet suppression).
/// Player-visible sessions are unaffected because (a) a default actor keeps
/// the flag TRUE — every per-apply move still broadcasts, and (b) even when
/// the executor opts the bot out, the executor's own throttled broadcast
/// still emits SCOneUnitMovementPacket to observers.
/// </summary>
[NotInParallel]
public class B5BroadcastMovementOptOutLeakageTests
{
    private sealed class PacketCaptureSession : ISession
    {
        public List<byte[]> CapturedPackets { get; } = [];

        public IPAddress Ip => IPAddress.Loopback;
        public uint SessionId => 1;
        public Socket Socket => null;
        public void SendPacket(byte[] packet) => CapturedPackets.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    private static int CapturedOpcodeCount(PacketCaptureSession capture, ushort opcode)
    {
        var count = 0;
        foreach (var bytes in capture.CapturedPackets)
        {
            try
            {
                var stream = new PacketStream();
                stream.Write(bytes);
                stream.ReadUInt16(); // length prefix
                stream.ReadByte();   // 0xdd
                stream.ReadByte();   // level (1)
                stream.ReadByte();   // hash (0)
                stream.ReadByte();   // count (0)
                if (stream.ReadUInt16() == opcode) // TypeId
                    count++;
            }
            catch
            {
                // malformed capture — skip
            }
        }

        return count;
    }

    [Test]
    public async Task PlayerVisibleSession_DefaultFlagTrue_PerApplyMoveStillBroadcasts()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor("b5-optout-default");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var capture = new PacketCaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
        WorldManager.Instance.AddVisibleObject(actor.Character);

        // The player-visible invariant: a fresh actor is NOT opted out.
        await Assert.That(actor.BroadcastMovement).IsTrue();

        // A direct contract Move (no roam executor in sight) rides the
        // per-apply client-authored movement model — observers get the
        // SCOneUnitMovementPacket stream.
        var request = actor.MoveTo(new Vector3(10, 0, 0), speed: 2f);
        for (var i = 0; i < 100 && request is { IsTerminal: false }; i++)
            actor.Tick(TimeSpan.FromMilliseconds(100));

        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);
        await Assert.That(CapturedOpcodeCount(capture, SCOffsets.SCOneUnitMovementPacket)).IsGreaterThanOrEqualTo(1);

        // Nothing outside the roam executor flipped the flag.
        await Assert.That(actor.BroadcastMovement).IsTrue();
    }

    [Test]
    public async Task OptOutConfinedToRoamExecutor_ObserverMovementStreamStillFlows()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, _) = GameplayActorTestRig.CreateActor("b5-optout-exec");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        var capture = new PacketCaptureSession();
        actor.Character.Connection = new GameConnection(capture) { ActiveChar = actor.Character };
        WorldManager.Instance.AddVisibleObject(actor.Character);

        var clock = new FakeTimeProvider();
        BotRoamStepExecutor executor = new()
        {
            GroundHeightProvider = (_, _) => 0f, // mock world: no heightmap data → clamp skipped
            ActorFactory = _ => actor,
            TimeProvider = clock,
            BroadcastInterval = TimeSpan.FromMilliseconds(200),
            ActiveCadence = TimeSpan.FromMilliseconds(100),
            RoamSpeed = 2f
        };
        executor.SetRoamRoute(
            actor.Character,
            new BotPath([new Vector3(10, 0, 0), new Vector3(20, 0, 0)], BotPath.LoopMode.Loop));

        // Step 1 arms the throttle baseline (no broadcast yet); subsequent
        // steps past BroadcastInterval with real displacement MUST broadcast.
        for (var i = 1; i <= 8; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(200));
            var next = await executor.StepAsync(new PlayerBotRuntime(actor.Character, "rig"), CancellationToken.None);
            await Assert.That(next).IsNotNull();
        }

        // The opt-out WAS applied — but only by the executor's step loop.
        await Assert.That(actor.BroadcastMovement).IsFalse();

        // ...and visibility was preserved: the executor's throttled
        // broadcast kept the observer stream flowing despite the opt-out.
        await Assert.That(CapturedOpcodeCount(capture, SCOffsets.SCOneUnitMovementPacket))
            .IsGreaterThanOrEqualTo(1)
            .Because("the headless opt-out suppresses only the per-apply packet; " +
                     "the roam executor's 4-6 Hz broadcast must keep player-visible sessions updated");
    }

    [Test]
    public async Task NonRoamContractActions_NeverTouchOptOutFlag()
    {
        AppConfiguration.Instance.World ??= new WorldConfig();
        var (actor, session) = GameplayActorTestRig.CreateActor("b5-optout-nonroam");
        GameplayActorTestRig.SetPosition(actor, new Vector3(0, 0, 0));
        WorldManager.Instance.AddVisibleObject(actor.Character);

        // Scenario-style direct actions (what M1M2/Economy replay scenarios
        // run outside headless roam) must leave the player-visible flag alone.
        var request = actor.MoveTo(new Vector3(4, 0, 0), speed: 2f);
        for (var i = 0; i < 100 && request is { IsTerminal: false }; i++)
            actor.Tick(TimeSpan.FromMilliseconds(100));
        await Assert.That(request.State).IsEqualTo(ActorLifecycleState.Completed);

        GameplayActorTestRig.SetPosition(actor, new Vector3(8, 0, 0));

        await Assert.That(actor.BroadcastMovement).IsTrue();
    }
}

/// <summary>
/// Seam: the E2E bridge metrics/control surface (BotDriveBridge — ping /
/// stats / metrics / scenario / save / drive JSON ops over 127.0.0.1 TCP).
/// Gate: disabled unless Config.Local.json "Bots"."EnableE2EBridge" or env
/// E2E_BRIDGE_ENABLED is set (prod config never sets it). Negative proof:
/// under player-session configuration the listener NEVER opens, and the
/// command dispatch (HandleCommand → CollectGateMetrics) has no callable
/// path other than through that listener.
/// </summary>
[NotInParallel]
public class B5BridgeMetricsLeakageTests
{
    [Test]
    public async Task BridgeDarkByDefault_PlayerSessionConfigNeverOpensListener()
    {
        // Explicitly-disabled env — the player-session shape (the variable is
        // absent in prod; "0" exercises the same not-enabled branch).
        var saved = Environment.GetEnvironmentVariable("E2E_BRIDGE_ENABLED");
        try
        {
            Environment.SetEnvironmentVariable("E2E_BRIDGE_ENABLED", "0");

            // If some earlier code path already started the bridge inside the
            // unit-test host, that ITSELF is leakage — fail loudly instead of
            // silently passing on an already-running listener.
            await Assert.That(BotDriveBridge.Instance.IsRunning)
                .IsFalse().Because("nothing in the unit-test host may have started the E2E bridge");

            BotDriveBridge.Instance.TryStart();

            await Assert.That(BotDriveBridge.Instance.IsRunning)
                .IsFalse().Because("with the gate closed (no env, no enabling config) TryStart must be a strict no-op");

            // And nothing is listening on the default bridge port either.
            var refused = false;
            try
            {
                using var probe = new TcpClient();
                await probe.ConnectAsync(IPAddress.Loopback, 1260).WaitAsync(TimeSpan.FromMilliseconds(500));
            }
            catch
            {
                refused = true; // connect failure is the expected dark state
            }

            await Assert.That(refused).IsTrue()
                .Because("a dark bridge must not accept loopback connections on the default port");
        }
        finally
        {
            Environment.SetEnvironmentVariable("E2E_BRIDGE_ENABLED", saved);
        }
    }

    [Test]
    public async Task BridgeControlSurface_DispatchAndMetricsArePrivate_NoGamePathEntryPoints()
    {
        var type = typeof(BotDriveBridge);

        // The ONLY public operation is TryStart (plus read-only state).
        var publicMethods = type.GetMethods(
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static |
                System.Reflection.BindingFlags.DeclaredOnly)
            .Where(m => !m.IsSpecialName) // drop property getters
            .Select(m => m.Name)
            .ToList();

        await Assert.That(publicMethods).IsEquivalentTo(["TryStart"])
            .Because("every extra public member would be a game-path entry point " +
                     "bypassing the EnableE2EBridge gate (G3-B5 finding)");

        // Command dispatch + the metrics collector stay private: they are
        // reachable ONLY from ServeClientAsync, which runs only for clients
        // accepted by the gated listener.
        foreach (var name in (string[])["HandleCommand", "CollectGateMetrics", "ServeClientAsync", "AcceptLoopAsync", "ReadConfig"])
        {
            var method = type.GetMethod(name,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.Static);
            await Assert.That(method).IsNotNull();
            await Assert.That(method!.IsPublic).IsFalse();
        }

        // Autonomy separation: the bridge instance is referenced ONLY by its
        // bootstrap (assembly-load, gated) — no scheduler/controller surface
        // consults it. That is a call-graph fact pinned by the audit report;
        // here we pin the visible half: no public static fields other than
        // compiler-generated bits.
        var publicStaticFields = type.GetFields(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
        await Assert.That(publicStaticFields).IsEmpty();
    }
}

/// <summary>
/// Seam: rig seed hooks — GameplayActorTestRig.Seed()/SeedTradeItemTemplate/
/// RegisterPlainItemTemplate etc. mutate process-wide singletons and item
/// template dictionaries. They live ENTIRELY inside the unit-test assembly;
/// no shipping assembly references them, so neither a player-visible session
/// nor an autonomous bot decision can ever consult one (compile-time fact,
/// asserted over the loaded assembly reference graph).
/// </summary>
public class B5RigSeedHookIsolationTests
{
    [Test]
    public async Task RigSeedHooks_ConfinedToTestAssemblies_ShippingAssembliesUnreferenced()
    {
        // The rig is a test-assembly type.
        await Assert.That(typeof(GameplayActorTestRig).Assembly).IsEqualTo(GetType().Assembly);

        // No shipping assembly references any test assembly.
        foreach (var shippingName in (string[])["AAEmu.Game", "AAEmu.Commons", "AAEmu.Login"])
        {
            var shipping = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => a.GetName().Name == shippingName);
            if (shipping == null)
                continue; // not loaded in this host — reference graph n/a

            var refs = shipping.GetReferencedAssemblies().Select(r => r.Name).ToHashSet();
            await Assert.That(refs.Contains("AAEmu.UnitTests")).IsFalse()
                .Because($"{shippingName} must never reference the rig seed hooks");
            await Assert.That(refs.Contains("AAEmu.IntegrationTests")).IsFalse()
                .Because($"{shippingName} must never reference E2E test surfaces");
        }
    }
}
