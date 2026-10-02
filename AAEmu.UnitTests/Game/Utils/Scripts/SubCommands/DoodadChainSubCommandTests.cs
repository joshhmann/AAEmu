using System.Drawing;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Chat;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;
namespace AAEmu.UnitTests.Game.Utils.Scripts.SubCommands;

public class DoodadChainSubCommandTests
{
    [Test]
    public void PreExecute_WhenChain_ShouldCallChainSubCommand()
    {
        var mockSubCommand = Mock.Of<ICommandV2>();
        var mockUnitCustomModelParams = Mock.Of<UnitCustomModelParams>(UnitCustomModelType.None);
        var fakeCharacter = new Character(mockUnitCustomModelParams.Object);

        var command = new TestCommand(new Dictionary<ICommandV2, string[]>
        {
            {
                mockSubCommand.Object, new string[]{ "sdf"}
            }
        });

        command.PreExecute(fakeCharacter, "test", ["sdf", "123"], new CharacterMessageOutput(fakeCharacter));

        mockSubCommand.PreExecute(fakeCharacter, "sdf", Is<string[]>(a => a.Length == 1 && a[0] == "123"), Any<IMessageOutput>()).WasCalled();
    }

    [Test]
    public void PreExecute_WhenChain_ShouldCallChainSubSubCommand()
    {
        var mockSubSubCommand = Mock.Of<ICommandV2>();
        var mockUnitCustomModelParams = Mock.Of<UnitCustomModelParams>(UnitCustomModelType.None);
        var fakeCharacter = new Character(mockUnitCustomModelParams.Object);

        var subCommand = new SubTestCommand(new Dictionary<ICommandV2, string[]>
        {
            {
                mockSubSubCommand.Object, new string[]{ "second"}
            }
        });

        var command = new TestCommand(new Dictionary<ICommandV2, string[]>
        {
            {
                subCommand, new string[]{ "first"}
            }
        });

        command.PreExecute(fakeCharacter, "test", ["first", "second", "parameter1second", "parameter2second"], new CharacterMessageOutput(fakeCharacter));

        mockSubSubCommand.PreExecute(fakeCharacter, "second", Is<string[]>(a => a.Length == 2 && a[0] == "parameter1second" && a[1] == "parameter2second"), Any<IMessageOutput>()).WasCalled();
    }

    [Test]
    public async Task Execute_WhenOnlyCommand_SendsTheHelpMessage()
    {
        // Empty register + empty args is the BACKWARDS-COMPATIBILITY branch of
        // PreExecute: there is no first argument to match a subcommand against,
        // so the command's own Execute runs — and the base Execute renders help.
        //
        // The old version asserted only "did not throw", so it stayed GREEN
        // with PreExecute gutted to `{ }`. The observable that matters is that
        // the dispatch REACHED the help renderer: nothing renders the
        // Description when PreExecute never calls Execute.
        var mockUnitCustomModelParams = Mock.Of<UnitCustomModelParams>(UnitCustomModelType.None);
        var fakeCharacter = new Character(mockUnitCustomModelParams.Object);
        var output = new RecordingMessageOutput();

        var testCommand = new TestCommand([]);
        testCommand.PreExecute(fakeCharacter, "doodad", Array.Empty<string>(), output);

        // Because: PreExecute with no args must fall through to Execute, which
        // renders the command help; nothing sends the Description otherwise.
        await Assert.That(output.Sent).Contains("Prefix Mock Command");
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    public async Task Execute_WhenSendingHelp_ListsEverySupportedSubcommand(int numberOfSupportedCommands)
    {
        // "help" as the first argument routes to SendHelpMessage, which lists
        // the registered aliases joined by "||".
        //
        // The previous version of this test had all three asserts commented out
        // under "// TODO: Fix these tests" and asserted NOTHING: it stayed GREEN
        // with both PreExecute AND SendHelpMessage gutted. They matched
        // `character.SendMessage(..., Color.LawnGreen)`, but
        // CharacterMessageOutput only ever calls the TWO-argument SendMessage
        // overload (it folds the colour into the string itself), so a
        // three-argument matcher on ICharacter could never match. IMessageOutput
        // is the seam production actually calls, and it receives the raw
        // message plus the colour — which is what this stub records.
        var mockCharacter = Mock.Of<ICharacter>();
        var supportedCommands = new Dictionary<ICommandV2, string[]>();
        var expectedCommands = new List<string>();
        for (var i = 0; i < numberOfSupportedCommands; i++)
        {
            var mockSubCommand = Mock.Of<ICommandV2>();
            supportedCommands.Add(mockSubCommand.Object, [$"command{i}"]);
            expectedCommands.Add($"command{i}");
        }

        var testCommand = new TestCommand(supportedCommands);
        var output = new RecordingMessageOutput();
        testCommand.PreExecute(mockCharacter.Object, "test", ["help"], output);

        var joined = string.Join("||", expectedCommands);
        await Assert.That(output.Sent).Contains($"Prefix Supported subcommands: <{joined}>")
            .Because("every registered subcommand alias must appear in the help listing");
        await Assert.That(output.Sent).Contains("Prefix For more details use /<command> <subcommand> help.");
        await Assert.That(output.Sent).Contains("Prefix Mock Command")
            .Because("the help block opens with the command's own description");
        await Assert.That(output.Colors).Contains(Color.LawnGreen)
            .Because("help output is the LawnGreen informational channel, not an error");
    }

    /// <summary>
    /// Records every (chatType, message, colour) triple production sends.
    /// CharacterMessageOutput is unusable as an observation seam here because
    /// it rewrites the message (it embeds the colour as a |c…|r code) and
    /// forwards to ICharacter; the raw triple is what the help contract is
    /// written against.
    /// </summary>
    private sealed class RecordingMessageOutput : IMessageOutput
    {
        public List<string> Sent { get; } = [];
        public List<Color?> Colors { get; } = [];

        public IEnumerable<string> Messages => Sent;
        public IEnumerable<string> ErrorMessages => [];

        public void SendMessage(string message) => SendMessage(ChatType.System, message, null);

        public void SendMessage(ChatType chatType, string message, Color? color = null)
        {
            Sent.Add(message);
            Colors.Add(color);
        }

        public void SendMessage(ICharacter target, string message) => SendMessage(message);
    }

    public class TestCommand : SubCommandBase
    {
        public TestCommand(Dictionary<ICommandV2, string[]> register) : base(register)
        {
            Title = "Prefix";
            Description = "Mock Command";
            CallPrefix = "Help Message";
        }
    }

    public class SubTestCommand : SubCommandBase
    {
        public SubTestCommand(Dictionary<ICommandV2, string[]> register) : base(register) { }
    }
}
