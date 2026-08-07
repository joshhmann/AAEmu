using System.Text.RegularExpressions;

using AAEmu.Game.Core.Managers.Bots;
using MySql.Data.MySqlClient;

namespace AAEmu.UnitTests.Utils.Mocks;

/// <summary>
/// In-memory recording IBotPersistenceDb for the dirty-flush rig.
///
/// Mirrors exactly the SQL shapes BotPersistenceManager generates
/// (REPLACE INTO / INSERT INTO / DELETE FROM / SELECT ... WHERE
/// `character_id` = @x) with REPLACE-by-PK semantics, so the rig can prove
/// metadata round-trips (flush → restore) without a live MySQL, and can
/// assert write counts: a per-step mark must produce ZERO statements until a
/// flush is requested.
/// </summary>
public sealed class BotPersistenceDbMock : IBotPersistenceDb
{
    private static readonly Regex s_tableRegex = new(@"^(?:REPLACE|INSERT)\s+INTO\s+`?(\w+)`?|^DELETE\s+FROM\s+`?(\w+)`?|^SELECT\s+.+?\s+FROM\s+`?(\w+)`?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_columnsRegex = new(@"\((`[^`]+`(?:,\s*`[^`]+`)*)\)\s*(?:VALUES|WHERE)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex s_whereRegex = new(@"WHERE\s+`(\w+)`\s*=\s*@(\w+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Table name → rows (each row: column → value, case-insensitive keys).</summary>
    public Dictionary<string, List<Dictionary<string, object>>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Every executed non-query statement, in order (the write-count assertion surface).</summary>
    public List<string> Statements { get; } = [];

    /// <summary>Number of executed non-query statements (writes).</summary>
    public int WriteCount => Statements.Count;

    /// <summary>Number of begun (and not yet committed/rolled back) transactions.</summary>
    public int OpenTransactionCount { get; private set; }

    /// <summary>True while inside a transaction (Begin without Commit/Rollback).</summary>
    public bool InTransaction { get; private set; }

    private long _nextScheduleId = 1;

    private static readonly Dictionary<string, string> s_primaryKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["playerbot_profile"] = "character_id",
        ["playerbot_schedule"] = "id",
        ["playerbot_activity"] = "character_id",
        ["playerbot_home"] = "character_id",
        ["playerbot_memory_flags"] = "character_id",
        ["playerbot_population_state"] = "character_id"
    };

    public Task BeginAsync(CancellationToken ct = default)
    {
        OpenTransactionCount++;
        InTransaction = true;
        return Task.CompletedTask;
    }

    public Task<int> ExecuteNonQueryAsync(string sql, IReadOnlyList<MySqlParameter>? parameters = null, CancellationToken ct = default)
    {
        Statements.Add(sql);
        var paramMap = ToParamMap(parameters);

        var match = s_tableRegex.Match(sql);
        var table = (match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Success ? match.Groups[2].Value : match.Groups[3].Value).ToLowerInvariant();
        if (table.Length == 0)
            throw new InvalidOperationException($"Mock cannot parse table from: {sql}");

        if (sql.StartsWith("REPLACE", StringComparison.OrdinalIgnoreCase) || sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase))
        {
            var row = BuildRow(sql, paramMap);
            ApplyUpsert(table, row);
        }
        else if (sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase))
        {
            var where = s_whereRegex.Match(sql);
            var column = where.Groups[1].Value.ToLowerInvariant();
            var value = paramMap[where.Groups[2].Value];
            var rows = Rows(table);
            rows.RemoveAll(r => Equals(r.TryGetValue(column, out var v) ? v : null, value));
        }
        else
        {
            throw new InvalidOperationException($"Mock cannot execute statement: {sql}");
        }

        return Task.FromResult(1);
    }

    public Task<List<Dictionary<string, object>>> QueryAsync(string sql, IReadOnlyList<MySqlParameter>? parameters = null, CancellationToken ct = default)
    {
        var match = s_tableRegex.Match(sql);
        var table = match.Groups[3].Value.ToLowerInvariant();
        if (table.Length == 0)
            throw new InvalidOperationException($"Mock cannot parse SELECT table from: {sql}");

        var paramMap = ToParamMap(parameters);
        var where = s_whereRegex.Match(sql);
        var column = where.Groups[1].Value.ToLowerInvariant();
        var value = paramMap[where.Groups[2].Value];

        var result = Rows(table)
            .Where(r => Equals(r.TryGetValue(column, out var v) ? v : null, value))
            .ToList();

        if (sql.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase))
            result = result.OrderBy(r => Convert.ToInt64(r["id"])).ToList();

        return Task.FromResult(result);
    }

    public Task CommitAsync(CancellationToken ct = default)
    {
        OpenTransactionCount--;
        InTransaction = false;
        return Task.CompletedTask;
    }

    public Task RollbackAsync(CancellationToken ct = default)
    {
        OpenTransactionCount--;
        InTransaction = false;
        return Task.CompletedTask;
    }

    public void Dispose()
    {
    }

    // ------------------------------------------------------------------ helpers

    private List<Dictionary<string, object>> Rows(string table) =>
        Tables.TryGetValue(table, out var rows) ? rows : Tables[table] = [];

    private static Dictionary<string, object?> ToParamMap(IReadOnlyList<MySqlParameter>? parameters)
    {
        var map = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (parameters == null)
            return map;
        foreach (var parameter in parameters)
            map[parameter.ParameterName.TrimStart('@')] = parameter.Value is DBNull ? null : parameter.Value;
        return map;
    }

    private static Dictionary<string, object> BuildRow(string sql, Dictionary<string, object?> paramMap)
    {
        var columnsMatch = s_columnsRegex.Match(sql);
        if (!columnsMatch.Success)
            throw new InvalidOperationException($"Mock cannot parse columns from: {sql}");

        var columns = columnsMatch.Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(c => c.Trim('`'))
            .ToArray();

        var valuesMatch = Regex.Match(sql, @"VALUES\s*\(([^)]+)\)", RegexOptions.IgnoreCase);
        var values = valuesMatch.Groups[1].Value
            .Split(',', StringSplitOptions.TrimEntries)
            .Select(v => v.Trim().TrimStart('@'))
            .ToArray();

        if (columns.Length != values.Length)
            throw new InvalidOperationException($"Mock column/value mismatch in: {sql}");

        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Length; i++)
            row[columns[i]] = paramMap[values[i]] ?? DBNull.Value;
        return row;
    }

    private void ApplyUpsert(string table, Dictionary<string, object> row)
    {
        var primaryKey = s_primaryKeys[table];
        var rows = Rows(table);

        // Schedule: id = 0 means "assign a fresh id" (mirrors AUTO_INCREMENT).
        if (table == "playerbot_schedule" && Convert.ToInt64(row["id"]) == 0)
            row["id"] = _nextScheduleId++;

        var existing = rows.FindIndex(r => Equals(r.TryGetValue(primaryKey, out var v) ? v : null, row[primaryKey]));
        if (existing >= 0)
            rows[existing] = row; // REPLACE semantics
        else
            rows.Add(row);
    }
}
