#nullable enable

using System.Drawing;
using System.Numerics;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot party &lt;follow|stay|role&gt; — companion bot party management command.
/// Subcommands:
///   follow [botId]           - sets companion follow mode
///   stay [botId]             - orders bot to hold position
///   role &lt;botId&gt; &lt;tank|healer|dps&gt; - assigns tactical party role
/// </summary>
public class BotPartySubCommand : SubCommandBase
{
    public BotPartySubCommand()
    {
        Title = "[Bot Party]";
        Description = "Manage companion bots: follow [botId], stay [botId], role <botId> <tank|healer|dps>";
        CallPrefix = $"{CommandManager.CommandPrefix}bot party";

        Register(new BotPartyFollowSubCommand(this), "follow");
        Register(new BotPartyStaySubCommand(this), "stay");
        Register(new BotPartyRoleSubCommand(this), "role");
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 0)
        {
            SendColorMessage(messageOutput, Color.Yellow, "Usage: /bot party <follow|stay|role> [options]");
            SendColorMessage(messageOutput, Color.White, "  follow [botId]                 - sets companion follow mode");
            SendColorMessage(messageOutput, Color.White, "  stay [botId]                   - orders bot to hold position");
            SendColorMessage(messageOutput, Color.White, "  role <botId> <tank|healer|dps> - assigns party role");
            return;
        }

        var action = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        switch (action)
        {
            case "follow":
                ExecuteFollow(character, subArgs, messageOutput);
                break;
            case "stay":
                ExecuteStay(character, subArgs, messageOutput);
                break;
            case "role":
                ExecuteRole(character, subArgs, messageOutput);
                break;
            default:
                SendColorMessage(messageOutput, Color.Red, $"Unknown action '{action}'. Valid actions: follow, stay, role.");
                break;
        }
    }

    public void ExecuteFollow(ICharacter character, string[] args, IMessageOutput messageOutput)
    {
        var botIdStr = args.FirstOrDefault();
        var bots = ResolveBots(character, botIdStr);
        if (bots.Count == 0)
        {
            SendColorMessage(messageOutput, Color.Red, "No companion bot found. Specify a botId, target a bot, or invite bots to your party.");
            return;
        }

        foreach (var (id, name, _) in bots)
        {
            BotFormation.SetCompanionMode(id, CompanionMode.Follow);
            SendColorMessage(messageOutput, Color.LawnGreen, $"[Bot Party] Bot {id} ({name}) set to follow mode.");
        }
    }

    public void ExecuteStay(ICharacter character, string[] args, IMessageOutput messageOutput)
    {
        var botIdStr = args.FirstOrDefault();
        var bots = ResolveBots(character, botIdStr);
        if (bots.Count == 0)
        {
            SendColorMessage(messageOutput, Color.Red, "No companion bot found. Specify a botId, target a bot, or invite bots to your party.");
            return;
        }

        foreach (var (id, name, pos) in bots)
        {
            BotFormation.SetCompanionMode(id, CompanionMode.Stay, pos);
            SendColorMessage(messageOutput, Color.LawnGreen, $"[Bot Party] Bot {id} ({name}) ordered to hold position at ({pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}).");
        }
    }

    public void ExecuteRole(ICharacter character, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 2)
        {
            SendColorMessage(messageOutput, Color.Red, "Usage: /bot party role <botId> <tank|healer|dps>");
            return;
        }

        var botIdStr = args[0];
        var roleStr = args[1].ToLowerInvariant();

        if (!TryParseRole(roleStr, out var role))
        {
            SendColorMessage(messageOutput, Color.Red, $"Unknown role '{roleStr}'. Valid roles: tank, healer, dps.");
            return;
        }

        var bots = ResolveBots(character, botIdStr);
        if (bots.Count == 0)
        {
            if (uint.TryParse(botIdStr, out var rawId) && rawId > 0)
            {
                BotFormation.SetCompanionRole(rawId, role);
                SendColorMessage(messageOutput, Color.LawnGreen, $"[Bot Party] Bot {rawId} assigned role {role}.");
                return;
            }

            SendColorMessage(messageOutput, Color.Red, $"Bot '{botIdStr}' could not be found.");
            return;
        }

        foreach (var (id, name, _) in bots)
        {
            BotFormation.SetCompanionRole(id, role);
            SendColorMessage(messageOutput, Color.LawnGreen, $"[Bot Party] Bot {id} ({name}) assigned role {role}.");
        }
    }

    private static bool TryParseRole(string roleStr, out CompanionRole role)
    {
        switch (roleStr)
        {
            case "tank":
                role = CompanionRole.Tank;
                return true;
            case "healer":
                role = CompanionRole.Healer;
                return true;
            case "dps":
                role = CompanionRole.Dps;
                return true;
            default:
                role = default;
                return false;
        }
    }

    private static List<(uint Id, string Name, Vector3 Position)> ResolveBots(ICharacter issuer, string? botIdStr)
    {
        var result = new List<(uint Id, string Name, Vector3 Position)>();

        // 1. Explicit botId or name provided
        if (!string.IsNullOrWhiteSpace(botIdStr))
        {
            var worldManager = AAEmu.Commons.Utils.SingletonContainer.ServiceProvider?.GetService(typeof(IWorldManager)) as IWorldManager;

            if (uint.TryParse(botIdStr, out var botId))
            {
                if (worldManager?.GetCharacterById(botId) is Character found)
                {
                    result.Add((found.Id, found.Name, found.Transform.World.Position));
                    return result;
                }

                var botManager = AAEmu.Commons.Utils.SingletonContainer.ServiceProvider?.GetService(typeof(IPlayerBotManager)) as IPlayerBotManager;
                if (botManager != null && botManager.TryGet(botId, out var runtime) && runtime != null)
                {
                    result.Add((runtime.CharacterId, runtime.Character.Name, runtime.Character.Transform.World.Position));
                    return result;
                }

                result.Add((botId, $"Bot-{botId}", issuer is Character ic ? ic.Transform.World.Position : Vector3.Zero));
                return result;
            }

            if (worldManager?.GetCharacter(botIdStr) is Character foundByName)
            {
                result.Add((foundByName.Id, foundByName.Name, foundByName.Transform.World.Position));
                return result;
            }

            return result;
        }

        // 2. Implicit: check issuer's current target
        if (issuer is Character issuerChar && issuerChar.CurrentTarget is Character targetChar && targetChar.Id != issuerChar.Id)
        {
            result.Add((targetChar.Id, targetChar.Name, targetChar.Transform.World.Position));
            return result;
        }

        // 3. Implicit: all bots in issuer's party
        if (issuer is Character partyLeader)
        {
            var teamManager = AAEmu.Commons.Utils.SingletonContainer.ServiceProvider?.GetService(typeof(ITeamManager)) as ITeamManager;
            var team = teamManager?.GetActiveTeamByUnit(partyLeader.Id);
            if (team != null)
            {
                var botManager = AAEmu.Commons.Utils.SingletonContainer.ServiceProvider?.GetService(typeof(IPlayerBotManager)) as IPlayerBotManager;
                var wm = AAEmu.Commons.Utils.SingletonContainer.ServiceProvider?.GetService(typeof(IWorldManager)) as IWorldManager;
                foreach (var member in team.Members)
                {
                    if (member.Character == null || member.Character.Id == partyLeader.Id) continue;
                    if (botManager != null && botManager.TryGet(member.Character.Id, out var botRuntime) && botRuntime != null)
                    {
                        result.Add((botRuntime.CharacterId, botRuntime.Character.Name, botRuntime.Character.Transform.World.Position));
                    }
                    else if (wm?.GetCharacterById(member.Character.Id) is Character memberChar)
                    {
                        result.Add((memberChar.Id, memberChar.Name, memberChar.Transform.World.Position));
                    }
                }
            }
        }

        return result;
    }
}

public class BotPartyFollowSubCommand : SubCommandBase
{
    private readonly BotPartySubCommand _parent;

    public BotPartyFollowSubCommand(BotPartySubCommand parent)
    {
        _parent = parent;
        Title = "[Bot Party Follow]";
        Description = "Set companion bot to follow mode: /bot party follow [botId]";
        CallPrefix = $"{CommandManager.CommandPrefix}bot party follow";
        AddParameter(new StringSubCommandParameter("botId", "botId", false));
    }

    public override void Execute(ICharacter character, string triggerArgument, IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var botId = GetOptionalParameterValue<string>(parameters, "botId", string.Empty);
        var args = !string.IsNullOrWhiteSpace(botId) ? new[] { botId } : Array.Empty<string>();
        _parent.ExecuteFollow(character, args, messageOutput);
    }
}

public class BotPartyStaySubCommand : SubCommandBase
{
    private readonly BotPartySubCommand _parent;

    public BotPartyStaySubCommand(BotPartySubCommand parent)
    {
        _parent = parent;
        Title = "[Bot Party Stay]";
        Description = "Order companion bot to hold position: /bot party stay [botId]";
        CallPrefix = $"{CommandManager.CommandPrefix}bot party stay";
        AddParameter(new StringSubCommandParameter("botId", "botId", false));
    }

    public override void Execute(ICharacter character, string triggerArgument, IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var botId = GetOptionalParameterValue<string>(parameters, "botId", string.Empty);
        var args = !string.IsNullOrWhiteSpace(botId) ? new[] { botId } : Array.Empty<string>();
        _parent.ExecuteStay(character, args, messageOutput);
    }
}

public class BotPartyRoleSubCommand : SubCommandBase
{
    private readonly BotPartySubCommand _parent;

    public BotPartyRoleSubCommand(BotPartySubCommand parent)
    {
        _parent = parent;
        Title = "[Bot Party Role]";
        Description = "Assign party role to bot: /bot party role <botId> <tank|healer|dps>";
        CallPrefix = $"{CommandManager.CommandPrefix}bot party role";
        AddParameter(new StringSubCommandParameter("botId", "botId", true));
        AddParameter(new StringSubCommandParameter("role", "tank||healer||dps", true));
    }

    public override void Execute(ICharacter character, string triggerArgument, IDictionary<string, ParameterValue> parameters, IMessageOutput messageOutput)
    {
        var botId = parameters["botId"].As<string>();
        var role = parameters["role"].As<string>();
        _parent.ExecuteRole(character, [botId, role], messageOutput);
    }
}
