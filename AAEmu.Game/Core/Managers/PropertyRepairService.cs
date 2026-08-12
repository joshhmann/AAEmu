using AAEmu.Commons.Utils.DB;
using AAEmu.Game.Models.Game.Housing;
using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>Result of an audit or repair pass.</summary>
public sealed record PropertyRepairReport(
    IReadOnlyList<PropertyRepairIssue> Issues,
    int IssuesFixed,
    IReadOnlyList<string> AppliedActions);

/// <summary>
/// Administrative repair tooling for corrupted/lost property state (M3b-4):
/// loads the live housings + persistent doodads rows, runs the pure scanner,
/// and applies fixes directly to MySQL. Usable both from the in-game GM
/// command (/house repair) and by an operator against a stopped server.
///
/// Fixes applied (per issue kind):
///   InvalidTemplateHouse   → delete the housings row (it can never load)
///   OrphanedOwnerHouse     → delete the housings row (ownership unrecoverable)
///   DuplicateHouse         → delete the later duplicate, keep the lowest id
///   OrphanedBoundDoodad    → delete the doodad row (dangles into a dead house)
///   OrphanedDoodadOwner    → delete the doodad row (owner char is gone)
///   OutOfRangeBuildStep    → clamp current_step/current_action to the template
///                            build range (never delete — state is recoverable)
///
/// The in-memory HousingManager state is NOT touched — a live server should
/// re-run LoadPlayerHousing (restart) after a repair so memory and DB agree.
/// </summary>
public class PropertyRepairService
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Audit-only: scan the DB state and return findings without changing anything.
    /// </summary>
    public PropertyRepairReport Audit()
    {
        using var connection = MySQL.CreateConnection();
        return Audit(connection);
    }

    public PropertyRepairReport Audit(MySqlConnection connection)
    {
        var view = LoadState(connection);
        var issues = PropertyRepairScanner.Scan(view);
        return new PropertyRepairReport(issues, 0, []);
    }

    /// <summary>
    /// Audit + fix: scan, then apply the repair for every finding.
    /// </summary>
    public PropertyRepairReport Repair()
    {
        using var connection = MySQL.CreateConnection();
        var view = LoadState(connection);
        var issues = PropertyRepairScanner.Scan(view);
        var actions = new List<string>();

        // House id → template build step count (for clamps). Only needed for
        // OutOfRangeBuildStep; InvalidTemplate houses are deleted instead.
        var stepCounts = view.TemplateBuildStepCounts;

        foreach (var issue in issues)
        {
            switch (issue.Kind)
            {
                case PropertyRepairIssueKind.InvalidTemplateHouse:
                    DeleteHouseAndDoodads(connection, issue.TargetId, actions);
                    break;
                case PropertyRepairIssueKind.OrphanedOwnerHouse:
                    DeleteHouseAndDoodads(connection, issue.TargetId, actions);
                    break;
                case PropertyRepairIssueKind.DuplicateHouse:
                    DeleteHouseAndDoodads(connection, issue.TargetId, actions);
                    break;
                case PropertyRepairIssueKind.OrphanedBoundDoodad:
                    DeleteDoodad(connection, issue.TargetId, actions);
                    break;
                case PropertyRepairIssueKind.OrphanedDoodadOwner:
                    DeleteDoodad(connection, issue.TargetId, actions);
                    break;
                case PropertyRepairIssueKind.OutOfRangeBuildStep:
                    ClampBuildStep(connection, issue.TargetId, stepCounts, actions);
                    break;
                default:
                    Logger.Warn($"Property repair: unknown issue kind {issue.Kind} for target {issue.TargetId} — skipped");
                    break;
            }
        }

        return new PropertyRepairReport(issues, actions.Count, actions);
    }

    // ------------------------------------------------------------- state load

    private static PropertyStateView LoadState(MySqlConnection connection)
    {
        var houses = new List<HouseRow>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, account_id, owner, template_id, x, y, z, current_step, current_action FROM housings";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                houses.Add(new HouseRow(
                    reader.GetUInt32("id"),
                    reader.GetUInt32("account_id"),
                    reader.GetUInt32("owner"),
                    reader.GetUInt32("template_id"),
                    reader.GetFloat("x"),
                    reader.GetFloat("y"),
                    reader.GetFloat("z"),
                    reader.GetInt32("current_step"),
                    reader.GetInt32("current_action")));
            }
        }

        var doodads = new List<DoodadRow>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText =
                "SELECT id, owner_id, owner_type, house_id FROM doodads WHERE owner_type = 3 OR owner_type = 254";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                doodads.Add(new DoodadRow(
                    reader.GetUInt32("id"),
                    reader.GetUInt32("owner_id"),
                    reader.GetByte("owner_type"),
                    reader.GetUInt32("house_id")));
            }
        }

        var characterIds = new HashSet<uint>();
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT id FROM characters";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                characterIds.Add(reader.GetUInt32("id"));
        }

        var templateIds = new HashSet<uint>();
        var buildSteps = new Dictionary<uint, int>();
        using (var cmd = AAEmu.Game.Utils.DB.SQLite.CreateConnection())
        {
            using (var sql = cmd.CreateCommand())
            {
                sql.CommandText = "SELECT id, (SELECT COUNT(*) FROM housing_build_steps hb WHERE hb.housing_id = housings.id) FROM housings";
                using var reader = sql.ExecuteReader();
                while (reader.Read())
                {
                    var templateId = (uint)reader.GetInt64(0);
                    templateIds.Add(templateId);
                    buildSteps[templateId] = reader.GetInt32(1);
                }
            }
        }

        return new PropertyStateView(houses, doodads, templateIds, characterIds, buildSteps);
    }

    // ------------------------------------------------------------- fixes

    private static void DeleteHouseAndDoodads(MySqlConnection connection, uint houseId, List<string> actions)
    {
        using var tx = connection.BeginTransaction();
        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM doodads WHERE house_id = @id";
            cmd.Parameters.AddWithValue("@id", houseId);
            cmd.ExecuteNonQuery();
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM housings WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", houseId);
            var deleted = cmd.ExecuteNonQuery();
            if (deleted > 0)
                actions.Add($"deleted house {houseId} (+ its bound doodads)");
        }

        tx.Commit();
    }

    private static void DeleteDoodad(MySqlConnection connection, uint doodadId, List<string> actions)
    {
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "DELETE FROM doodads WHERE id = @id";
        cmd.Parameters.AddWithValue("@id", doodadId);
        var deleted = cmd.ExecuteNonQuery();
        if (deleted > 0)
            actions.Add($"deleted orphaned doodad {doodadId}");
    }

    private static void ClampBuildStep(MySqlConnection connection, uint houseId,
        IReadOnlyDictionary<uint, int> stepCounts, List<string> actions)
    {
        // Need the template id + current values to compute the clamp.
        uint templateId = 0;
        int currentStep = 0, currentAction = 0;
        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "SELECT template_id, current_step, current_action FROM housings WHERE id = @id";
            cmd.Parameters.AddWithValue("@id", houseId);
            using var reader = cmd.ExecuteReader();
            if (!reader.Read())
                return;
            templateId = reader.GetUInt32("template_id");
            currentStep = reader.GetInt32("current_step");
            currentAction = reader.GetInt32("current_action");
        }

        if (!stepCounts.TryGetValue(templateId, out var stepCount) || stepCount <= 0)
            return;

        var newStep = Math.Clamp(currentStep, -1, stepCount - 1);
        var newAction = Math.Max(0, currentAction);

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = "UPDATE housings SET current_step = @step, current_action = @action WHERE id = @id";
            cmd.Parameters.AddWithValue("@step", newStep);
            cmd.Parameters.AddWithValue("@action", newAction);
            cmd.Parameters.AddWithValue("@id", houseId);
            var updated = cmd.ExecuteNonQuery();
            if (updated > 0)
                actions.Add($"clamped house {houseId} build state {currentStep}/{currentAction} → {newStep}/{newAction}");
        }
    }
}
