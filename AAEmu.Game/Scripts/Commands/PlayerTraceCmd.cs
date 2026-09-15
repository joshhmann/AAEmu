using System.Drawing;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Scripts.SubCommands.PlayerTrace;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// Root GM/Admin command for the Player Action Tracer (/trace, /ptrace, /playertrace, /stoptrace).
/// </summary>
public class PlayerTraceCmd : SubCommandBase, ICommand, ICommandV2
{
    public string[] CommandNames { get; set; } = ["trace", "playertrace", "ptrace", "stoptrace"];

    private readonly PlayerTraceStopSubCommand _stopSubCommand;

    public PlayerTraceCmd()
    {
        Title = "[PlayerTrace]";
        Description = "Record server-visible semantic action traces of human and bot players";
        CallPrefix = $"{CommandManager.CommandPrefix}{CommandNames[0]}";

        _stopSubCommand = new PlayerTraceStopSubCommand();

        Register(new PlayerTraceStartSubCommand(), "start");
        Register(_stopSubCommand, "stop");
        Register(new PlayerTraceStatusSubCommand(), "status");
        Register(new PlayerTraceMarkSubCommand(), "mark", "note");
        Register(new PlayerTraceExportSubCommand(), "export");
    }

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public PlayerTraceCmd(Dictionary<ICommandV2, string[]> subcommands) : base(subcommands)
    {
        _stopSubCommand = new PlayerTraceStopSubCommand();
    }

    public string GetCommandLineHelp()
    {
        return $"<{string.Join("||", SupportedCommands)}>";
    }

    public string GetCommandHelpText()
    {
        return CallPrefix;
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        PreExecute(character, "", args, messageOutput);
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        // Direct shortcut: /stoptrace directly invokes stop
        if (triggerArgument.Equals("stoptrace", StringComparison.OrdinalIgnoreCase))
        {
            _stopSubCommand.Execute(character, triggerArgument, args, messageOutput);
            return;
        }

        // When typed with no subcommands (/trace), show status or helpful cheatsheet
        var (isActive, charName, scenario, elapsedMs, events, _) = PlayerTraceService.Instance.GetStatus();
        if (isActive)
        {
            SendColorMessage(messageOutput, Color.Cyan,
                $"[PlayerTrace] RECORDING: '{charName}' | Scenario: {scenario} | Time: {elapsedMs / 1000.0:F1}s | Events: {events}");
            SendColorMessage(messageOutput, Color.White,
                "Commands: /trace stop  |  /trace mark <note>  |  /trace status");
        }
        else
        {
            SendColorMessage(messageOutput, Color.LawnGreen, "[PlayerTrace] Player Action Tracer (Ready)");
            SendColorMessage(messageOutput, Color.White, "  /trace start [scenario]       - Start tracing yourself");
            SendColorMessage(messageOutput, Color.White, "  /trace start <bot> [scenario] - Start tracing another player or bot");
            SendColorMessage(messageOutput, Color.White, "  /trace stop (or /stoptrace)   - Stop recording and save JSONL");
            SendColorMessage(messageOutput, Color.White, "  /trace mark <note>            - Insert a note/checkpoint");
        }
    }
}
