using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Utils.Scripts;

namespace AAEmu.Game.Scripts.Commands;

/// <summary>
/// Administrative property-state repair tool (M3b-4): scans the live MySQL
/// housings + doodads state for corruption (invalid templates, orphaned
/// owners, orphaned bound doodads, duplicates, out-of-range build steps) and
/// applies fixes. GM-gated by default (unlisted command → access level 100).
///
/// Usage:
///   /house repair            → audit only, report findings, change nothing
///   /house repair fix        → audit + apply fixes
///
/// NOTE: fixes are applied at the DB level. On a live server, restart the
/// game afterwards so the in-memory HousingManager reloads the repaired rows
/// (LoadPlayerHousing) — memory and DB must agree.
/// </summary>
public class PropertyRepairCmd : ICommand
{
    public string[] CommandNames { get; set; } = ["house_repair", "property_repair"];

    private static readonly PropertyRepairService RepairService = new();

    public void OnLoad()
    {
        CommandManager.Instance.Register(CommandNames, this);
    }

    public string GetCommandLineHelp()
    {
        return "[fix]";
    }

    public string GetCommandHelpText()
    {
        return "Audits housings/doodads state for corruption; 'fix' applies repairs";
    }

    public void Execute(Character character, string[] args, IMessageOutput messageOutput)
    {
        var applyFix = args.Length > 0 && (args[0] == "fix" || args[0] == "repair");

        try
        {
            var report = applyFix ? RepairService.Repair() : RepairService.Audit();

            if (report.Issues.Count == 0)
            {
                messageOutput.SendMessage($"|cFF00FF00[House Repair] No property issues found ({(applyFix ? "fix" : "audit")} mode).|r");
                return;
            }

            messageOutput.SendMessage($"|cFFFFAA00[House Repair] {report.Issues.Count} issue(s) found ({(applyFix ? "fix" : "audit")} mode):|r");
            foreach (var issue in report.Issues)
            {
                messageOutput.SendMessage($"  [{issue.Kind}] id={issue.TargetId}: {issue.Detail}");
            }

            if (applyFix && report.AppliedActions.Count > 0)
            {
                messageOutput.SendMessage($"|cFF00FF00[House Repair] Applied {report.AppliedActions.Count} fix(es):|r");
                foreach (var action in report.AppliedActions)
                    messageOutput.SendMessage($"  - {action}");
                messageOutput.SendMessage("|cFF00FF00[House Repair] Restart the game server so the in-memory housing state reloads.|r");
            }
            else if (applyFix)
            {
                messageOutput.SendMessage("|cFFFFAA00[House Repair] No fixes were applicable (issues may be informational).|r");
            }
            else
            {
                messageOutput.SendMessage("|cFFFFAA00[House Repair] Run '/house repair fix' to apply fixes.|r");
            }
        }
        catch (Exception e)
        {
            CommandManager.SendErrorText(this, messageOutput, $"Property repair failed: {e.Message}");
        }
    }
}
