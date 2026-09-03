using System.Text.RegularExpressions;

namespace AAEmu.ArchaeologyMcp;

/// <summary>
/// Strict read-only SQL allow-list for the archaeology server.
///
/// Only single SELECT / WITH / EXPLAIN / schema-read PRAGMA statements are
/// accepted. Everything else — INSERT/UPDATE/DELETE/DROP/ALTER/CREATE/
/// REPLACE/ATTACH/DETACH/VACUUM/REINDEX, PRAGMA mutations, multi-statement
/// batches, and obfuscated comments/semicolons — is rejected before it ever
/// reaches the connection. The connection itself is additionally opened
/// Mode=ReadOnly as a defense-in-depth backstop.
/// </summary>
public static class SqlGuard
{
    /// <summary>Statement keywords that are never allowed, in any position.</summary>
    private static readonly string[] ForbiddenKeywords =
    [
        "INSERT", "UPDATE", "DELETE", "DROP", "ALTER", "CREATE", "REPLACE",
        "ATTACH", "DETACH", "VACUUM", "REINDEX", "TRUNCATE", "GRANT", "REVOKE",
    ];

    /// <summary>
    /// Schema-read PRAGMA names that are allowed. Everything else is
    /// rejected — including value-assignment pragmas (journal_mode=WAL,
    /// user_version=1, …) and write-state pragmas.
    /// </summary>
    private static readonly string[] AllowedPragmas =
    [
        "table_info", "table_list", "index_list", "index_info", "index_xinfo",
        "foreign_key_list", "database_list", "collation_list", "function_list",
        "module_list", "pragma_list", "compile_options", "encoding",
    ];

    /// <summary>
    /// Table-valued pragma functions (pragma_&lt;name&gt;) that mutate or
    /// expose write state — rejected even inside SELECT, where the PRAGMA
    /// allow-list does not apply.
    /// </summary>
    private static readonly string[] ForbiddenPragmaFunctions =
    [
        "pragma_wal_checkpoint", "pragma_optimize", "pragma_integrity_check",
        "pragma_quick_check", "pragma_shrink_memory", "pragma_incremental_vacuum",
        "pragma_writable_schema", "pragma_query_only", "pragma_foreign_keys",
        "pragma_journal_mode", "pragma_synchronous", "pragma_locking_mode",
        "pragma_cache_size", "pragma_page_size", "pragma_auto_vacuum",
        "pragma_temp_store", "pragma_mmap_size", "pragma_busy_timeout",
        "pragma_user_version", "pragma_application_id", "pragma_schema_version",
        "pragma_data_version", "pragma_read_uncommitted", "pragma_recursive_triggers",
        "pragma_secure_delete", "pragma_cell_size_check", "pragma_count_changes",
        "pragma_full_column_names", "pragma_short_column_names",
        "pragma_reverse_unordered_selects", "pragma_freelist_count",
        "pragma_page_count", "pragma_page_free_list",
    ];

    private static readonly Regex CommentPattern = new(@"--[^\r\n]*|/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex LeadingKeywordPattern = new(@"^\s*([A-Za-z]+)", RegexOptions.Compiled);
    private static readonly Regex PragmaNamePattern = new(@"^\s*PRAGMA\s+([A-Za-z_][A-Za-z0-9_]*)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PragmaFunctionPattern = new(@"\bpragma_[A-Za-z_][A-Za-z0-9_]*\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Validates a single SQL statement. Returns null when allowed, otherwise
    /// a human-readable rejection reason.
    /// </summary>
    public static string? Validate(string sql)
    {
        if (string.IsNullOrWhiteSpace(sql))
            return "empty SQL statement";

        // Reject any statement containing comments — obfuscation is not
        // allowed to hide keywords or semicolons.
        if (CommentPattern.IsMatch(sql))
            return "SQL comments are not allowed";

        // Reject multi-statement batches (any semicolon outside a string literal).
        if (ContainsSemicolonOutsideStrings(sql))
            return "multi-statement SQL is not allowed (single statement only)";

        // Reject forbidden keywords anywhere in the statement (word-boundary).
        foreach (var keyword in ForbiddenKeywords)
        {
            if (Regex.IsMatch(sql, $@"\b{keyword}\b", RegexOptions.IgnoreCase))
                return $"statement contains forbidden keyword: {keyword}";
        }

        // Reject table-valued pragma functions that mutate or expose write
        // state (pragma_wal_checkpoint, pragma_optimize, …) — these bypass
        // the PRAGMA allow-list when used inside SELECT.
        foreach (var match in PragmaFunctionPattern.Matches(sql).Cast<Match>())
        {
            var fn = match.Value.ToLowerInvariant();
            if (ForbiddenPragmaFunctions.Contains(fn))
                return $"forbidden pragma function: {fn}";
        }

        // Reject load_extension — arbitrary native code execution.
        if (Regex.IsMatch(sql, @"\bload_extension\s*\(", RegexOptions.IgnoreCase))
            return "load_extension is not allowed";

        var leadingMatch = LeadingKeywordPattern.Match(sql);
        if (!leadingMatch.Success)
            return "unrecognized SQL statement";

        var leading = leadingMatch.Groups[1].Value.ToUpperInvariant();
        return leading switch
        {
            "SELECT" or "WITH" => null,
            "EXPLAIN" => ValidateExplain(sql),
            "PRAGMA" => ValidatePragma(sql),
            _ => $"statement type not allowed: {leading} (SELECT/WITH/EXPLAIN/schema PRAGMA only)",
        };
    }

    /// <summary>
    /// EXPLAIN must not be used to smuggle a mutating PRAGMA past the
    /// allow-list (e.g. EXPLAIN PRAGMA journal_mode=WAL). The explained
    /// statement is validated with the same rules. The optional
    /// "QUERY PLAN" form (EXPLAIN QUERY PLAN SELECT …) is allowed.
    /// </summary>
    private static string? ValidateExplain(string sql)
    {
        var rest = sql[LeadingKeywordPattern.Match(sql).Length..].TrimStart();
        if (rest.Length == 0)
            return "EXPLAIN requires a statement";

        // EXPLAIN QUERY PLAN <stmt> — skip the QUERY PLAN prefix.
        if (rest.StartsWith("QUERY", StringComparison.OrdinalIgnoreCase))
        {
            var afterQuery = rest["QUERY".Length..].TrimStart();
            if (afterQuery.StartsWith("PLAN", StringComparison.OrdinalIgnoreCase))
                rest = afterQuery["PLAN".Length..].TrimStart();
        }

        if (rest.Length == 0)
            return "EXPLAIN requires a statement";

        var inner = LeadingKeywordPattern.Match(rest);
        if (!inner.Success)
            return "unrecognized EXPLAIN target";

        var innerLeading = inner.Groups[1].Value.ToUpperInvariant();
        if (innerLeading == "PRAGMA")
            return ValidatePragma(rest);

        if (innerLeading is "SELECT" or "WITH")
            return null;

        return $"EXPLAIN target not allowed: {innerLeading}";
    }

    private static string? ValidatePragma(string sql)
    {
        var match = PragmaNamePattern.Match(sql);
        if (!match.Success)
            return "malformed PRAGMA statement";

        var name = match.Groups[1].Value.ToLowerInvariant();
        if (!AllowedPragmas.Contains(name))
            return $"PRAGMA {name} is not allowed (schema-read pragmas only)";

        // Schema-read pragmas take a single table/index argument; anything
        // with an '=' (value assignment) is a mutation and is rejected.
        var rest = sql[match.Length..].Trim();
        if (rest.Contains('='))
            return "PRAGMA value assignment is not allowed";

        // The remainder must be exactly one parenthesized argument
        // (e.g. table_info(npcs)) or empty (e.g. database_list) — no
        // trailing garbage, no second argument.
        if (rest.Length > 0)
        {
            if (!rest.StartsWith('(') || !rest.EndsWith(')'))
                return "malformed PRAGMA argument";
            var inner = rest[1..^1].Trim();
            if (inner.Length == 0 || inner.Contains(',') || inner.Contains('(') || inner.Contains(')'))
                return "PRAGMA argument must be a single identifier";
        }

        return null;
    }

    private static bool ContainsSemicolonOutsideStrings(string sql)
    {
        var inSingle = false;
        var inDouble = false;
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (c == '\'' && !inDouble)
                inSingle = !inSingle;
            else if (c == '"' && !inSingle)
                inDouble = !inDouble;
            else if (c == ';' && !inSingle && !inDouble)
                return true;
        }

        return false;
    }
}
