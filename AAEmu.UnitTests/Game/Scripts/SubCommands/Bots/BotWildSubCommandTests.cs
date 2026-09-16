using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Scripts.SubCommands.Bots;
using AAEmu.Game.Utils.Scripts;
using AAEmu.UnitTests.Utils.Mocks;

namespace AAEmu.UnitTests.Game.Scripts.SubCommands.Bots;

public class BotWildSubCommandTests
{
    private sealed class TestMessageOutput : IMessageOutput
    {
        public List<(System.Drawing.Color Color, string Message)> SentMessages { get; } = [];
        public List<string> ErrorMessages { get; } = [];

        public IEnumerable<string> Messages => SentMessages.Select(m => m.Message);
        IEnumerable<string> IMessageOutput.ErrorMessages => ErrorMessages;

        public void SendMessage(string message) => SentMessages.Add((System.Drawing.Color.White, message));
        public void SendMessage(AAEmu.Game.Models.Game.Chat.ChatType chatType, string message, System.Drawing.Color? color = null) => SentMessages.Add((color ?? System.Drawing.Color.White, message));
        public void SendMessage(ICharacter target, string message) => SentMessages.Add((System.Drawing.Color.White, message));
        public void SendMessage(ICharacter character, System.Drawing.Color color, string message) => SentMessages.Add((color, message));
    }

    [Test]
    public async Task Execute_NoArgs_ShowsUsage()
    {
        var cmd = new BotWildSubCommand();
        var output = new TestMessageOutput();
        var character = new CharacterMock { Id = 1, Name = "Tester" };

        cmd.Execute(character, "wild", [], output);

        await Assert.That(output.Messages.Any(m => m.Contains("Usage: /bot wild"))).IsTrue();
    }

    [Test]
    public async Task Execute_List_DisplaysCatalogedPois()
    {
        var cmd = new BotWildSubCommand();
        var output = new TestMessageOutput();
        var character = new CharacterMock { Id = 1, Name = "Tester" };

        cmd.Execute(character, "wild", ["list"], output);

        await Assert.That(output.Messages.Any(m => m.Contains("cataloged wilderness POIs"))).IsTrue();
        await Assert.That(output.Messages.Any(m => m.Contains("wild-farm-solzreed-plateau"))).IsTrue();
    }

    [Test]
    public async Task Execute_Nearest_ResolvesClosestPoi()
    {
        var cmd = new BotWildSubCommand();
        var output = new TestMessageOutput();
        var character = new CharacterMock { Id = 1, Name = "Tester" };
        character.Transform.Local.SetPosition(14200f, 14650f, 130f);

        cmd.Execute(character, "wild", ["nearest"], output);

        await Assert.That(output.Messages.Any(m => m.Contains("Closest to"))).IsTrue();
        await Assert.That(output.Messages.Any(m => m.Contains("Solzreed Secret High Plateau"))).IsTrue();
    }
}
