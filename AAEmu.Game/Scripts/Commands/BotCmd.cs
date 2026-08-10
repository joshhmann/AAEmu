using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Scripts.SubCommands.Bots;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// Root GM command for live bot operations (P1 card t_f216710e): add /
/// remove / list / here / go — so bots can be added, removed and relocated
/// live without redeploys. Admin-gated by default: AccessLevelManager
/// returns 100 for unlisted command names, so /bot requires an effective
/// access level of 100+ unless the operator lowers it in AccessLevels.json.
/// All operations run through the existing PlayerBotManager + provisioning
/// + lifecycle (no parallel bot path, AGENTS.md #9/#10).
/// </summary>
public class BotCmd : SubCommandBase, ICommand, ICommandV2
{
    public string[] CommandNames { get; set; } = ["bot"];

    public BotCmd()
    {
        Title = "[Bot]";
        Description = "Root command to manage player bots (add/remove/list/here/go)";
        CallPrefix = $"{CommandManager.CommandPrefix}{CommandNames[0]}";

        Register(new BotListSubCommand(), "list");
        Register(new BotHereSubCommand(), "here");
        Register(new BotAddSubCommand(), "add");
        Register(new BotRemoveSubCommand(), "remove", "rm");
        Register(new BotGoSubCommand(), "go");
        Register(new BotToSubCommand(), "to");
    }

    public void OnLoad()
    {
        CommandManager.Instance.Register("bot", this);
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
        throw new InvalidOperationException(
            $"A {nameof(ICommandV2)} implementation should not be used as ICommand interface");
    }
}
