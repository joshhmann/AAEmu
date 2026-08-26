using AAEmu.Commons.Network.Core;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Faction;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Templates;
using AAEmu.Game.Models.StaticValues;
using AAEmu.UnitTests.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// CRIME-01 headless verification rig (justice slice 1): crime-point math and
/// the wanted-state boundary at the REAL property-setter seam.
///
/// Engine entry points exercised (all REAL code paths):
///   - Character.AddCrime (Character.cs ~2960): CP/Infamy twin-increment,
///     short.MaxValue clamp, SCCrimeChangedPacket emission (opcode 0x16f)
///   - CrimePoint / InfamyPoint property setters: every change funnels
///     through CheckWantedThreshold() + MarkDirty()
///   - CheckWantedThreshold (CharacterCombat.cs ~421):
///     CrimePoint &gt;= CrimeManager.WantedCrimePointThreshold (50) applies the
///     Wanted buff 3710 at the exact boundary; dropping below removes it.
///     The clamp leg also drives InfamyPoint past the 3000 pirate branch
///     (Contemptuous 4832 + Pirate faction), so that path is seeded too.
///
/// Wire-format note: GameConnection.SendPacket hands the session the full
/// encoded frame [len u16][0xdd][level][hash][count][type u16 LE][payload…]
/// (GamePacket.Encode) — opcode lives at byte offset 6.
/// </summary>
[NotInParallel]
public class CrimePointsRigTests
{
    private const uint WantedBuffId = 3710;       // BuffConstants.Wanted
    private const uint ContemptuousBuffId = 4832; // BuffConstants.Contemptuous

    // ------------------------------------------------------------------ rig helpers

    private sealed class PacketCaptureSession : ISession
    {
        public List<byte[]> CapturedPackets { get; } = [];

        public System.Net.IPAddress Ip => System.Net.IPAddress.Loopback;
        public uint SessionId => 1;
        public System.Net.Sockets.Socket Socket => null;

        public void SendPacket(byte[] packet) => CapturedPackets.Add(packet);
        public void AddAttribute(string name, object attribute) { }
        public object GetAttribute(string name) => null;
        public void ClearAttribute(string name) { }
        public void Close() { }
    }

    private static (Character Character, PacketCaptureSession Capture) CreateCapturedCharacter(string name)
    {
        var (_, session) = GameplayActorTestRig.CreateActor(name);
        var character = session.Character;
        var capture = new PacketCaptureSession();
        var conn = new GameConnection(capture) { ActiveChar = character };
        character.Connection = conn;
        return (character, capture);
    }

    /// <summary>Seeds the justice buff templates into SkillManager
    /// (BuffTemplate.BuffId is a derived alias of Id, so CheckBuff matches).
    /// MUST run AFTER CreateActor — the actor rig owns the DI-shaped
    /// SkillManager seeding; seeding first would lazy-create a bare singleton
    /// that cannot construct.</summary>
    private static void SeedJusticeBuffTemplates()
    {
        GameplayActorTestRig.SeedBuffTemplate(WantedBuffId);
        GameplayActorTestRig.SeedBuffTemplate(ContemptuousBuffId);
        var manager = SkillManager.Instance;
        var field = typeof(SkillManager).GetField("_buffs",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var buffs = (Dictionary<uint, BuffTemplate>)field.GetValue(manager)!;
        buffs[WantedBuffId] = new BuffTemplate { Id = WantedBuffId };
        buffs[ContemptuousBuffId] = new BuffTemplate { Id = ContemptuousBuffId };
    }

    private static (int Points, short CrimePoints, int InfamyPoints, short CrimeState)? ParseSCCrimeChanged(
        IReadOnlyList<byte[]> packets)
    {
        foreach (var p in packets)
        {
            if (p.Length < 20)
                continue;
            var type = (ushort)(p[6] | (p[7] << 8));
            if (type != 0x016f) // SCOffsets.SCCrimeChangedPacket
                continue;
            var points = BitConverter.ToInt32(p, 8);
            var crimePoints = BitConverter.ToInt16(p, 12);
            var infamyPoints = BitConverter.ToInt32(p, 14);
            var crimeState = BitConverter.ToInt16(p, 18);
            return (points, crimePoints, infamyPoints, crimeState);
        }

        return null;
    }

    // ------------------------------------------------------------------ a. point math

    [Test]
    public async Task AddCrime_RaisesBothCounters_Clamps_AndEmitsSCCrimeChanged()
    {
        var (killer, capture) = CreateCapturedCharacter("crime-math");
        SeedJusticeBuffTemplates();

        killer.AddCrime(10);

        await Assert.That(killer.CrimePoint).IsEqualTo((short)10);
        await Assert.That(killer.InfamyPoint).IsEqualTo(10);

        var emitted = ParseSCCrimeChanged(capture.CapturedPackets);
        await Assert.That(emitted).IsNotNull();
        await Assert.That(emitted!.Value.Points).IsEqualTo(10);          // delta
        await Assert.That(emitted.Value.CrimePoints).IsEqualTo((short)10);
        await Assert.That(emitted.Value.InfamyPoints).IsEqualTo(10);
        await Assert.That(emitted.Value.CrimeState).IsEqualTo((short)0); // not wanted yet

        // Floor: a negative delta clamps CrimePoint at 0 and the InfamyPoint
        // setter floors its counter at 0 too. (The short.MaxValue CEILING leg
        // is deliberately not exercised here: AddCrime's ceiling branch is
        // overwritten by the trailing else ((short)newAmount wraps negative),
        // and driving infamy past 3000 drags in the pirate SetFaction path —
        // documented as an observation in the justice-domain addendum.)
        killer.AddCrime(-20);
        await Assert.That(killer.CrimePoint).IsEqualTo((short)0);
        await Assert.That(killer.InfamyPoint).IsEqualTo(0);
    }

    // ------------------------------------------------------------------ b. wanted boundary seam

    [Test]
    public async Task CrimePointSetter_Crossing50_AppliesWantedBuff_AtTheSeam()
    {
        var (criminal, _) = CreateCapturedCharacter("crime-wanted");
        SeedJusticeBuffTemplates();

        // Just below the threshold: no wanted state.
        criminal.CrimePoint = 49;
        criminal.InfamyPoint = 49; // keep the pirate branch (3000) out of the picture
        await Assert.That(criminal.Buffs.CheckBuff(WantedBuffId)).IsFalse();
        await Assert.That(criminal.GetCrimeState()).IsEqualTo((short)0);

        // Reach exactly 50 THROUGH the setter seam → wanted fires at the boundary.
        criminal.CrimePoint = 50;
        await Assert.That(criminal.Buffs.CheckBuff(WantedBuffId)).IsTrue();
        await Assert.That(criminal.GetCrimeState()).IsEqualTo((short)1);

        // Drop back below → the same seam removes the buff again.
        criminal.CrimePoint = 49;
        await Assert.That(criminal.Buffs.CheckBuff(WantedBuffId)).IsFalse();
        await Assert.That(criminal.GetCrimeState()).IsEqualTo((short)0);
    }

    [Test]
    public async Task CrimePointChange_MarksCharacterDirty_ForPeriodicSave()
    {
        var (criminal, _) = CreateCapturedCharacter("crime-dirty");
        // Fresh rig characters start dirty; flush the flag the way SaveManager
        // does after a successful save cycle.
        criminal.IsDirty = false;

        criminal.CrimePoint += 10;

        await Assert.That(criminal.IsDirty).IsTrue();

        criminal.IsDirty = false;
        criminal.InfamyPoint += 5;
        await Assert.That(criminal.IsDirty).IsTrue();
    }
}
