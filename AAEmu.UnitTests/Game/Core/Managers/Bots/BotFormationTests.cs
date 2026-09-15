using System.Drawing;
using System.Numerics;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Scripts.SubCommands.Bots;
using AAEmu.Game.Utils.Scripts;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

[NotInParallel]
public class BotFormationTests
{
    private sealed class TestMessageOutput : IMessageOutput
    {
        public List<(Color Color, string Message)> SentMessages { get; } = [];
        public List<string> ErrorMessages { get; } = [];

        public IEnumerable<string> Messages => SentMessages.Select(m => m.Message);
        IEnumerable<string> IMessageOutput.ErrorMessages => ErrorMessages;

        public void SendMessage(string message) => SentMessages.Add((Color.White, message));
        public void SendMessage(ChatType chatType, string message, Color? color = null) => SentMessages.Add((color ?? Color.White, message));
        public void SendMessage(ICharacter target, string message) => SentMessages.Add((Color.White, message));
        public void SendMessage(ICharacter character, Color color, string message) => SentMessages.Add((color, message));
    }

    private static Character CreateMockLeader(uint id, Vector3 pos, float yawDegrees)
    {
        var leader = new CharacterMock
        {
            Id = id,
            ObjId = id + 0x1000,
            Name = $"Leader_{id}"
        };
        leader.Transform.Local.SetPosition(pos);
        leader.Transform.Local.SetRotation(0f, 0f, yawDegrees);
        return leader;
    }

    // -------------------------------------------------------------------------
    // 1. Formation Slot Geometry (North, East, South, West)
    // -------------------------------------------------------------------------

    [Test]
    public async Task CalculateSlotPosition_FacingNorth_PlacesSlotsBehindAndFlanking()
    {
        // Facing North (0 deg): forward = (0, 1, 0), right = (1, 0, 0)
        // Leader at origin (0, 0, 0)
        var leader = CreateMockLeader(101, Vector3.Zero, 0f);
        const float spacing = 2.5f;

        // Slot 0: row 0, side -1 (left). Back 2.5m, Left 2.5m. Base: (-2.5, -2.5, 0)
        var pos0Base = BotFormation.CalculateSlotPosition(leader, 0, spacing, applyOrganicOffset: false);
        await Assert.That(MathF.Abs(pos0Base.X - (-2.5f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos0Base.Y - (-2.5f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos0Base.Z - 0f)).IsLessThan(0.001f);

        // Slot 1: row 0, side +1 (right). Back 2.5m, Right 2.5m. Base: (2.5, -2.5, 0)
        var pos1Base = BotFormation.CalculateSlotPosition(leader, 1, spacing, applyOrganicOffset: false);
        await Assert.That(MathF.Abs(pos1Base.X - 2.5f)).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos1Base.Y - (-2.5f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos1Base.Z - 0f)).IsLessThan(0.001f);

        // Slot 2: row 1, side -1 (left). Back 5.0m, Left 2.5m. Base: (-2.5, -5.0, 0)
        var pos2Base = BotFormation.CalculateSlotPosition(leader, 2, spacing, applyOrganicOffset: false);
        await Assert.That(MathF.Abs(pos2Base.X - (-2.5f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos2Base.Y - (-5.0f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos2Base.Z - 0f)).IsLessThan(0.001f);

        // With organic offset enabled: Y must stay -2.5 and -5.0, X has stable variation within ±0.35m
        var pos0 = BotFormation.CalculateSlotPosition(leader, 0, spacing, applyOrganicOffset: true);
        var pos1 = BotFormation.CalculateSlotPosition(leader, 1, spacing, applyOrganicOffset: true);
        var pos2 = BotFormation.CalculateSlotPosition(leader, 2, spacing, applyOrganicOffset: true);

        await Assert.That(MathF.Abs(pos0.Y - (-2.5f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos0.X - (-2.5f))).IsLessThanOrEqualTo(BotFormation.DefaultMaxOrganicOffset);

        await Assert.That(MathF.Abs(pos1.Y - (-2.5f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos1.X - 2.5f)).IsLessThanOrEqualTo(BotFormation.DefaultMaxOrganicOffset);

        await Assert.That(MathF.Abs(pos2.Y - (-5.0f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos2.X - (-2.5f))).IsLessThanOrEqualTo(BotFormation.DefaultMaxOrganicOffset);
    }

    [Test]
    public async Task CalculateSlotPosition_FacingEast_PlacesSlotsBehindAndFlanking()
    {
        // Facing East (90 deg): forward = (1, 0, 0), right = (0, -1, 0)
        // Leader at (10, 20, 5)
        var leader = CreateMockLeader(102, new Vector3(10f, 20f, 5f), 90f);
        const float spacing = 2.5f;

        // Behind leader is -X (10 - 2.5 = 7.5).
        // Slot 0: side -1 (left) -> +right * -2.5 = -(0, -1, 0) * 2.5 = (0, +2.5, 0). Base Y = 22.5
        var pos0Base = BotFormation.CalculateSlotPosition(leader, 0, spacing, applyOrganicOffset: false);
        await Assert.That(MathF.Abs(pos0Base.X - 7.5f)).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos0Base.Y - 22.5f)).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos0Base.Z - 5f)).IsLessThan(0.001f);

        // Slot 1: side +1 (right) -> +right * 2.5 = (0, -2.5, 0). Base Y = 17.5
        var pos1Base = BotFormation.CalculateSlotPosition(leader, 1, spacing, applyOrganicOffset: false);
        await Assert.That(MathF.Abs(pos1Base.X - 7.5f)).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos1Base.Y - 17.5f)).IsLessThan(0.001f);

        // Slot 2: row 1, side -1 -> back 5.0m. Base X = 5.0, Base Y = 22.5
        var pos2Base = BotFormation.CalculateSlotPosition(leader, 2, spacing, applyOrganicOffset: false);
        await Assert.That(MathF.Abs(pos2Base.X - 5.0f)).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos2Base.Y - 22.5f)).IsLessThan(0.001f);

        // With organic offset enabled: X is exactly -backDistance, Y has variation within ±0.35m
        var pos0 = BotFormation.CalculateSlotPosition(leader, 0, spacing, applyOrganicOffset: true);
        await Assert.That(MathF.Abs(pos0.X - 7.5f)).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos0.Y - 22.5f)).IsLessThanOrEqualTo(BotFormation.DefaultMaxOrganicOffset);
    }

    [Test]
    public async Task CalculateSlotPosition_FacingSouthAndWest_PreservesGeometricInvariance()
    {
        // Facing South (180 deg): forward = (0, -1, 0), right = (-1, 0, 0)
        var leaderSouth = CreateMockLeader(103, Vector3.Zero, 180f);
        var pos0South = BotFormation.CalculateSlotPosition(leaderSouth, 0, 2.0f, applyOrganicOffset: false);
        // Behind South is +Y (0 - (-1)*2 = +2). Left is East (+X)
        await Assert.That(MathF.Abs(pos0South.Y - 2.0f)).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos0South.X - 2.0f)).IsLessThan(0.001f);

        // Facing West (270 deg): forward = (-1, 0, 0), right = (0, 1, 0)
        var leaderWest = CreateMockLeader(104, Vector3.Zero, 270f);
        var pos0West = BotFormation.CalculateSlotPosition(leaderWest, 0, 2.0f, applyOrganicOffset: false);
        // Behind West is +X (+2). Left is South (-Y)
        await Assert.That(MathF.Abs(pos0West.X - 2.0f)).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(pos0West.Y - (-2.0f))).IsLessThan(0.001f);
    }

    // -------------------------------------------------------------------------
    // 2. Deterministic Hash Repeatability & Distinctness
    // -------------------------------------------------------------------------

    [Test]
    public async Task StableSignedVariation_IsDeterministicAndRepeatable()
    {
        const uint leaderId = 505;
        const int slot = 2;

        float sample1 = BotFormation.StableSignedVariation(leaderId, slot);
        float sample2 = BotFormation.StableSignedVariation(leaderId, slot);
        float sample3 = BotFormation.StableSignedVariation(leaderId, slot);

        await Assert.That(sample1).IsEqualTo(sample2);
        await Assert.That(sample2).IsEqualTo(sample3);

        var leader = CreateMockLeader(leaderId, new Vector3(100f, 100f, 10f), 45f);
        var posCall1 = BotFormation.CalculateSlotPosition(leader, slot);
        var posCall2 = BotFormation.CalculateSlotPosition(leader, slot);

        await Assert.That(posCall1).IsEqualTo(posCall2);
    }

    [Test]
    public async Task StableSignedVariation_DiffersAcrossSlotsAndLeaders()
    {
        const uint leaderId1 = 12345;
        const uint leaderId2 = 67890;

        float slot0 = BotFormation.StableSignedVariation(leaderId1, 0);
        float slot1 = BotFormation.StableSignedVariation(leaderId1, 1);
        float slot2 = BotFormation.StableSignedVariation(leaderId1, 2);
        float slot3 = BotFormation.StableSignedVariation(leaderId1, 3);

        // Slots produce distinct offsets
        await Assert.That(slot0).IsNotEqualTo(slot1);
        await Assert.That(slot1).IsNotEqualTo(slot2);
        await Assert.That(slot2).IsNotEqualTo(slot3);

        // Different leaders produce distinct offsets for the same slot
        float leader2Slot0 = BotFormation.StableSignedVariation(leaderId2, 0);
        await Assert.That(slot0).IsNotEqualTo(leader2Slot0);
    }

    [Test]
    public async Task StableSignedVariation_RespectsBoundedRange()
    {
        for (uint leaderId = 1; leaderId <= 10; leaderId++)
        {
            for (int slot = 0; slot < 20; slot++)
            {
                float variation = BotFormation.StableSignedVariation(leaderId, slot, 0.35f);
                await Assert.That(variation).IsGreaterThanOrEqualTo(-0.35f);
                await Assert.That(variation).IsLessThanOrEqualTo(0.35f);
            }
        }
    }

    // -------------------------------------------------------------------------
    // 3. Spacing Boundaries and Null Guards
    // -------------------------------------------------------------------------

    [Test]
    public async Task CalculateSlotPosition_NullLeader_ThrowsArgumentNullException()
    {
        await Assert.That(() => BotFormation.CalculateSlotPosition(null!, 0))
            .Throws<ArgumentNullException>();
    }

    [Test]
    public async Task CalculateSlotPosition_NegativeSlot_ThrowsArgumentOutOfRangeException()
    {
        var leader = CreateMockLeader(1, Vector3.Zero, 0f);
        await Assert.That(() => BotFormation.CalculateSlotPosition(leader, -1))
            .Throws<ArgumentOutOfRangeException>();
    }

    [Test]
    public async Task CalculateSlotPosition_NonPositiveSpacing_ThrowsArgumentOutOfRangeException()
    {
        var leader = CreateMockLeader(1, Vector3.Zero, 0f);
        await Assert.That(() => BotFormation.CalculateSlotPosition(leader, 0, baseSpacing: 0f))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => BotFormation.CalculateSlotPosition(leader, 0, baseSpacing: -1f))
            .Throws<ArgumentOutOfRangeException>();
    }

    // -------------------------------------------------------------------------
    // 4. CalculateSpreadPosition Tests
    // -------------------------------------------------------------------------

    [Test]
    public async Task CalculateSpreadPosition_CalculatesCenteredGridCorrectly()
    {
        // Facing North (0 deg): forward = (0, 1, 0), right = (1, 0, 0)
        var leader = CreateMockLeader(200, Vector3.Zero, 0f);
        const float baseDistance = 3.0f;
        const int columns = 2;
        const float spacing = 2.0f;

        // Slot 0: row 0, col 0 -> latOffset = (0 - 0.5) * 2 = -1.0; back = 3.0 + 0 = 3.0
        var s0 = BotFormation.CalculateSpreadPosition(leader, 0, 4, baseDistance, columns, spacing);
        await Assert.That(MathF.Abs(s0.X - (-1.0f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(s0.Y - (-3.0f))).IsLessThan(0.001f);

        // Slot 1: row 0, col 1 -> latOffset = (1 - 0.5) * 2 = +1.0; back = 3.0 + 0 = 3.0
        var s1 = BotFormation.CalculateSpreadPosition(leader, 1, 4, baseDistance, columns, spacing);
        await Assert.That(MathF.Abs(s1.X - 1.0f)).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(s1.Y - (-3.0f))).IsLessThan(0.001f);

        // Slot 2: row 1, col 0 -> latOffset = -1.0; back = 3.0 + 2.0 = 5.0
        var s2 = BotFormation.CalculateSpreadPosition(leader, 2, 4, baseDistance, columns, spacing);
        await Assert.That(MathF.Abs(s2.X - (-1.0f))).IsLessThan(0.001f);
        await Assert.That(MathF.Abs(s2.Y - (-5.0f))).IsLessThan(0.001f);
    }

    [Test]
    public async Task CalculateSpreadPosition_GuardsBoundaries()
    {
        var leader = CreateMockLeader(201, Vector3.Zero, 0f);

        await Assert.That(() => BotFormation.CalculateSpreadPosition(null!, 0, 4, 3f, 2, 2f))
            .Throws<ArgumentNullException>();
        await Assert.That(() => BotFormation.CalculateSpreadPosition(leader, -1, 4, 3f, 2, 2f))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => BotFormation.CalculateSpreadPosition(leader, 0, 0, 3f, 2, 2f))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => BotFormation.CalculateSpreadPosition(leader, 0, 4, 3f, 0, 2f))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => BotFormation.CalculateSpreadPosition(leader, 0, 4, -1f, 2, 2f))
            .Throws<ArgumentOutOfRangeException>();
        await Assert.That(() => BotFormation.CalculateSpreadPosition(leader, 0, 4, 3f, 2, 0f))
            .Throws<ArgumentOutOfRangeException>();
    }

    // -------------------------------------------------------------------------
    // 5. Companion State and BotPartySubCommand Tests
    // -------------------------------------------------------------------------

    [Test]
    public async Task CompanionStateManagement_TracksModeAndRole()
    {
        BotFormation.ClearCompanionStates();
        const uint botId = 777;

        // Default mode is Follow, default role is Dps
        await Assert.That(BotFormation.GetCompanionMode(botId)).IsEqualTo(CompanionMode.Follow);
        await Assert.That(BotFormation.GetCompanionRole(botId)).IsEqualTo(CompanionRole.Dps);

        // Set Stay mode
        var stayPos = new Vector3(10f, 20f, 30f);
        BotFormation.SetCompanionMode(botId, CompanionMode.Stay, stayPos);
        await Assert.That(BotFormation.GetCompanionMode(botId)).IsEqualTo(CompanionMode.Stay);
        await Assert.That(BotFormation.GetCompanionState(botId)?.StayPosition).IsEqualTo(stayPos);

        // Set Follow mode again
        BotFormation.SetCompanionMode(botId, CompanionMode.Follow);
        await Assert.That(BotFormation.GetCompanionMode(botId)).IsEqualTo(CompanionMode.Follow);

        // Set Tank role
        BotFormation.SetCompanionRole(botId, CompanionRole.Tank);
        await Assert.That(BotFormation.GetCompanionRole(botId)).IsEqualTo(CompanionRole.Tank);

        // Set Healer role
        BotFormation.SetCompanionRole(botId, CompanionRole.Healer);
        await Assert.That(BotFormation.GetCompanionRole(botId)).IsEqualTo(CompanionRole.Healer);
    }

    [Test]
    public async Task BotPartySubCommand_ExecutesFollowStayRoleCorrectly()
    {
        BotFormation.ClearCompanionStates();
        var subCommand = new BotPartySubCommand();
        var output = new TestMessageOutput();
        var issuer = CreateMockLeader(1, Vector3.Zero, 0f);

        // 1. Help message when no arguments provided
        subCommand.Execute(issuer, "party", [], output);
        await Assert.That(output.SentMessages.Count).IsGreaterThanOrEqualTo(1);
        await Assert.That(output.SentMessages.Any(m => m.Message.Contains("Usage"))).IsTrue();
        output.SentMessages.Clear();

        // 2. Follow command with explicit botId
        subCommand.Execute(issuer, "party", ["follow", "901"], output);
        await Assert.That(BotFormation.GetCompanionMode(901)).IsEqualTo(CompanionMode.Follow);
        await Assert.That(output.SentMessages.Any(m => m.Color == Color.LawnGreen && m.Message.Contains("follow"))).IsTrue();
        output.SentMessages.Clear();

        // 3. Stay command with explicit botId
        subCommand.Execute(issuer, "party", ["stay", "901"], output);
        await Assert.That(BotFormation.GetCompanionMode(901)).IsEqualTo(CompanionMode.Stay);
        await Assert.That(output.SentMessages.Any(m => m.Color == Color.LawnGreen && m.Message.Contains("hold position"))).IsTrue();
        output.SentMessages.Clear();

        // 4. Role assignment commands
        subCommand.Execute(issuer, "party", ["role", "901", "tank"], output);
        await Assert.That(BotFormation.GetCompanionRole(901)).IsEqualTo(CompanionRole.Tank);
        await Assert.That(output.SentMessages.Any(m => m.Color == Color.LawnGreen && m.Message.Contains("Tank"))).IsTrue();
        output.SentMessages.Clear();

        subCommand.Execute(issuer, "party", ["role", "901", "healer"], output);
        await Assert.That(BotFormation.GetCompanionRole(901)).IsEqualTo(CompanionRole.Healer);
        await Assert.That(output.SentMessages.Any(m => m.Color == Color.LawnGreen && m.Message.Contains("Healer"))).IsTrue();
        output.SentMessages.Clear();

        subCommand.Execute(issuer, "party", ["role", "901", "dps"], output);
        await Assert.That(BotFormation.GetCompanionRole(901)).IsEqualTo(CompanionRole.Dps);
        await Assert.That(output.SentMessages.Any(m => m.Color == Color.LawnGreen && m.Message.Contains("Dps"))).IsTrue();
        output.SentMessages.Clear();

        // 5. Unknown role rejected
        subCommand.Execute(issuer, "party", ["role", "901", "mage"], output);
        await Assert.That(output.SentMessages.Any(m => m.Color == Color.Red && m.Message.Contains("Unknown role"))).IsTrue();
        output.SentMessages.Clear();

        // 6. Unknown action rejected
        subCommand.Execute(issuer, "party", ["dance", "901"], output);
        await Assert.That(output.SentMessages.Any(m => m.Color == Color.Red && m.Message.Contains("Unknown action"))).IsTrue();
    }
}
