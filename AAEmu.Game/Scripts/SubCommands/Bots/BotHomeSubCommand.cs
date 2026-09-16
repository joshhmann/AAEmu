#nullable enable

using System.Drawing;
using System.Numerics;
using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.Bots.Goap;
using AAEmu.Game.Core.Managers.Bots.Goap.Actions;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Utils.Scripts;
using AAEmu.Game.Utils.Scripts.SubCommands;
using Microsoft.Extensions.DependencyInjection;

namespace AAEmu.Game.Scripts.SubCommands.Bots;

/// <summary>
/// /bot home &lt;status|run|claim|build|kit&gt; [botName] — commands and monitors bot homestead progression.
/// Subcommands:
///   status [botName]       - displays homestead and housing progress for the specified bot (or all active bots)
///   run &lt;botName&gt;          - initiates autonomous GOAP homestead progression for the bot
///   claim &lt;botName&gt;        - prioritizes claiming an 8x8 scarecrow garden plot in the nearest housing zone
///   build &lt;botName&gt;        - prioritizes crafting material packs and constructing the homestead
///   kit &lt;botName&gt;          - supplies the starter Straw Hat Scarecrow Garden design and tax certificates
/// </summary>
public class BotHomeSubCommand : SubCommandBase
{
    public BotHomeSubCommand()
    {
        Title = "[Bot Homestead]";
        Description = "Manage player bot homestead and housing progression: status, run, claim, build, kit";
        CallPrefix = $"{CommandManager.CommandPrefix}bot home";
    }

    public override void Execute(ICharacter character, string triggerArgument, string[] args, IMessageOutput messageOutput)
    {
        if (args.Length == 0)
        {
            SendColorMessage(messageOutput, Color.Yellow, "Usage: /bot home <status|run|claim|build|kit> [options]");
            SendColorMessage(messageOutput, Color.White, "  status [botName]       - shows land ownership, materials, and construction progress");
            SendColorMessage(messageOutput, Color.White, "  run <botName>          - starts autonomous homestead loop (claim -> harvest -> build)");
            SendColorMessage(messageOutput, Color.White, "  claim <botName>        - orders bot to survey and place an 8x8 scarecrow garden plot");
            SendColorMessage(messageOutput, Color.White, "  build <botName>        - orders bot to craft packs and construct home");
            SendColorMessage(messageOutput, Color.White, "  kit <botName>          - gives starter scarecrow garden design and 10x tax certificates");
            return;
        }

        var action = args[0].ToLowerInvariant();
        var subArgs = args.Skip(1).ToArray();

        switch (action)
        {
            case "status":
                ExecuteStatus(subArgs, messageOutput);
                break;
            case "run":
                ExecuteRun(subArgs, messageOutput);
                break;
            case "claim":
                ExecuteClaim(subArgs, messageOutput);
                break;
            case "build":
                ExecuteBuild(subArgs, messageOutput);
                break;
            case "kit":
                ExecuteKit(subArgs, messageOutput);
                break;
            default:
                SendColorMessage(messageOutput, Color.Red, $"Unknown action '{action}'. Valid actions: status, run, claim, build, kit.");
                break;
        }
    }

    private static IPlayerBotManager? GetBotManager()
    {
        var sp = SingletonContainer.ServiceProvider;
        return sp?.GetService<IPlayerBotManager>();
    }

    private static PlayerBotRuntime? ResolveBot(string botName)
    {
        var manager = GetBotManager();
        return manager?.GetAll().FirstOrDefault(r => r.Character.Name.Equals(botName, StringComparison.OrdinalIgnoreCase));
    }

    private void ExecuteStatus(string[] args, IMessageOutput messageOutput)
    {
        var manager = GetBotManager();
        if (manager == null)
        {
            SendColorMessage(messageOutput, Color.Red, "PlayerBotManager not available.");
            return;
        }

        var bots = manager.GetAll();
        if (args.Length >= 1)
        {
            var target = ResolveBot(args[0]);
            if (target == null)
            {
                SendColorMessage(messageOutput, Color.Red, $"Bot '{args[0]}' not found.");
                return;
            }
            bots = [target];
        }

        if (bots.Count == 0)
        {
            SendColorMessage(messageOutput, Color.Yellow, "No active player bots.");
            return;
        }

        SendColorMessage(messageOutput, Color.LightGreen, $"[Bot Homestead] Status of {bots.Count} bot(s):");
        var housing = HousingManager.PeekInstance;

        foreach (var bot in bots)
        {
            var ch = bot.Character;
            var pos = ch.Transform.World.Position;
            var ownedHouses = housing?.GetAllHouses().Where(h => h.OwnerId == ch.Id).ToList() ?? [];
            var hasScarecrowDesign = ch.Inventory?.Bag?.GetItemsSnapshot().Any(i => i != null && (i.TemplateId == AcquireScarecrowAction.ScarecrowDesignTemplateId || i.TemplateId == AcquireScarecrowAction.ScarecrowDesignId)) ?? false;
            var taxCerts = ch.Inventory?.Bag?.GetItemsSnapshot().Where(i => i != null && i.TemplateId == AcquireScarecrowAction.TaxCertificateTemplateId).Sum(i => i.Count) ?? 0;
            var timberCount = ch.Inventory?.Bag?.GetItemsSnapshot().Where(i => i != null && (i.TemplateId == 14 || i.TemplateId == 15 || i.Template?.CategoryId == (int)ItemCategory.Lumber)).Sum(i => i.Count) ?? 0;
            var hasPack = (ch.Equipment?.GetItemBySlot((byte)EquipmentItemSlotType.Backpack) != null) ||
                          (ch.Inventory?.Bag?.GetItemsSnapshot().Any(i => i?.Template != null && (i.Template.CategoryId == (int)ItemCategory.Trade_Pack || i.Template.CategoryId == (int)ItemCategory.Body_Pack)) ?? false);

            SendColorMessage(messageOutput, Color.Cyan, $"  Bot '{ch.Name}' (id {ch.Id}, Level {ch.Level}, {ch.Race}):");
            SendColorMessage(messageOutput, Color.White, $"    Location: {pos.X:F1}, {pos.Y:F1}, {pos.Z:F1}");
            SendColorMessage(messageOutput, Color.White, $"    Inventory: Scarecrow Design: {(hasScarecrowDesign ? "YES" : "NO")}, Tax Certs: {taxCerts}, Timber/Logs: {timberCount}, Pack Equipped: {(hasPack ? "YES" : "NO")}");

            if (ownedHouses.Count == 0)
            {
                SendColorMessage(messageOutput, Color.Yellow, "    Plot: None (Starter intent: Claim small 8x8 farm plot)");
            }
            else
            {
                foreach (var h in ownedHouses)
                {
                    var isFinished = h.CurrentStep == -1;
                    var statusStr = isFinished ? "Constructed / Complete" : $"Under Construction (Step {h.CurrentStep})";
                    var hPos = h.Transform.World.Position;
                    SendColorMessage(messageOutput, Color.LawnGreen, $"    Plot ID {h.Id} at ({hPos.X:F1}, {hPos.Y:F1}, {hPos.Z:F1}): {statusStr}");
                }
            }
        }
    }

    private void ExecuteRun(string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 1)
        {
            SendColorMessage(messageOutput, Color.Red, "Usage: /bot home run <botName>");
            return;
        }

        var bot = ResolveBot(args[0]);
        if (bot == null)
        {
            SendColorMessage(messageOutput, Color.Red, $"Bot '{args[0]}' not found.");
            return;
        }

        // Ensure starter kit is present if bot does not have plot or design
        var ch = bot.Character;
        var hasScarecrowDesign = ch.Inventory?.Bag?.GetItemsSnapshot().Any(i => i != null && (i.TemplateId == AcquireScarecrowAction.ScarecrowDesignTemplateId || i.TemplateId == AcquireScarecrowAction.ScarecrowDesignId)) ?? false;
        if (!hasScarecrowDesign && ch.Inventory?.Bag != null)
        {
            ch.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Gm, AcquireScarecrowAction.ScarecrowDesignTemplateId, 1, 1);
            ch.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Gm, AcquireScarecrowAction.TaxCertificateTemplateId, 10, 1);
            SendColorMessage(messageOutput, Color.Yellow, $"[Bot Homestead] Seeded starter Straw Hat Scarecrow Garden design and 10x Tax Certificates to '{ch.Name}'.");
        }

        var housing = HousingManager.PeekInstance;
        var hasPlot = housing?.GetAllHouses().Any(h => h.OwnerId == ch.Id) ?? false;

        SendColorMessage(messageOutput, Color.LawnGreen,
            $"[Bot Homestead] Autonomous homestead progression activated for '{ch.Name}'. Target: {(hasPlot ? "Erect Home" : "Claim 8x8 Scarecrow Garden Plot")}.");
    }

    private void ExecuteClaim(string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 1)
        {
            SendColorMessage(messageOutput, Color.Red, "Usage: /bot home claim <botName>");
            return;
        }

        var bot = ResolveBot(args[0]);
        if (bot == null)
        {
            SendColorMessage(messageOutput, Color.Red, $"Bot '{args[0]}' not found.");
            return;
        }

        var ch = bot.Character;
        if (ch.Inventory?.Bag != null)
        {
            var hasDesign = ch.Inventory.Bag.GetItemsSnapshot().Any(i => i != null && i.TemplateId == AcquireScarecrowAction.ScarecrowDesignTemplateId);
            if (!hasDesign)
            {
                ch.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Gm, AcquireScarecrowAction.ScarecrowDesignTemplateId, 1, 1);
                ch.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Gm, AcquireScarecrowAction.TaxCertificateTemplateId, 10, 1);
            }
        }

        SendColorMessage(messageOutput, Color.LawnGreen,
            $"[Bot Homestead] Bot '{ch.Name}' ordered to claim small 8x8 scarecrow garden plot in nearest housing zone.");
    }

    private void ExecuteBuild(string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 1)
        {
            SendColorMessage(messageOutput, Color.Red, "Usage: /bot home build <botName>");
            return;
        }

        var bot = ResolveBot(args[0]);
        if (bot == null)
        {
            SendColorMessage(messageOutput, Color.Red, $"Bot '{args[0]}' not found.");
            return;
        }

        var ch = bot.Character;
        SendColorMessage(messageOutput, Color.LawnGreen,
            $"[Bot Homestead] Bot '{ch.Name}' ordered to cultivate timber, craft material packs, and construct home.");
    }

    private void ExecuteKit(string[] args, IMessageOutput messageOutput)
    {
        if (args.Length < 1)
        {
            SendColorMessage(messageOutput, Color.Red, "Usage: /bot home kit <botName>");
            return;
        }

        var bot = ResolveBot(args[0]);
        if (bot == null)
        {
            SendColorMessage(messageOutput, Color.Red, $"Bot '{args[0]}' not found.");
            return;
        }

        var ch = bot.Character;
        if (ch.Inventory?.Bag != null)
        {
            ch.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Gm, AcquireScarecrowAction.ScarecrowDesignTemplateId, 1, 1);
            ch.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Gm, AcquireScarecrowAction.TaxCertificateTemplateId, 10, 1);
            SendColorMessage(messageOutput, Color.LawnGreen,
                $"[Bot Homestead] Granted Straw Hat Scarecrow Garden design (15596) and 10x Tax Certificates (8000001) to '{ch.Name}'.");
        }
        else
        {
            SendColorMessage(messageOutput, Color.Red, $"Bot '{ch.Name}' has no inventory bag.");
        }
    }
}
