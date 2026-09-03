using AAEmu.ArchaeologyMcp;

namespace AAEmu.UnitTests.ArchaeologyMcp;

/// <summary>
/// Defends the strict read-only SQL allow-list: SELECT/WITH/EXPLAIN and
/// schema-read PRAGMAs are accepted; every mutation keyword, multi-statement
/// batch, and obfuscation trick is rejected.
/// </summary>
[NotInParallel]
public class SqlGuardTests
{
    [Test]
    public async Task Select_IsAllowed()
    {
        await Assert.That(SqlGuard.Validate("SELECT * FROM npcs")).IsNull();
    }

    [Test]
    public async Task Select_WithWhereAndLimit_IsAllowed()
    {
        await Assert.That(SqlGuard.Validate("SELECT id, name FROM items WHERE id = 29040 LIMIT 5")).IsNull();
    }

    [Test]
    public async Task With_IsAllowed()
    {
        await Assert.That(SqlGuard.Validate("WITH x AS (SELECT 1) SELECT * FROM x")).IsNull();
    }

    [Test]
    public async Task Explain_IsAllowed()
    {
        await Assert.That(SqlGuard.Validate("EXPLAIN QUERY PLAN SELECT * FROM npcs")).IsNull();
    }

    [Test]
    public async Task Pragma_TableInfo_IsAllowed()
    {
        await Assert.That(SqlGuard.Validate("PRAGMA table_info(npcs)")).IsNull();
    }

    [Test]
    public async Task Pragma_IndexList_IsAllowed()
    {
        await Assert.That(SqlGuard.Validate("PRAGMA index_list(npcs)")).IsNull();
    }

    [Test]
    public async Task Insert_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("INSERT INTO npcs (id) VALUES (1)")).IsNotNull();
    }

    [Test]
    public async Task Update_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("UPDATE npcs SET name = 'x' WHERE id = 1")).IsNotNull();
    }

    [Test]
    public async Task Delete_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("DELETE FROM npcs WHERE id = 1")).IsNotNull();
    }

    [Test]
    public async Task Drop_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("DROP TABLE npcs")).IsNotNull();
    }

    [Test]
    public async Task Alter_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("ALTER TABLE npcs ADD COLUMN x INTEGER")).IsNotNull();
    }

    [Test]
    public async Task Create_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("CREATE TABLE t (id INTEGER)")).IsNotNull();
    }

    [Test]
    public async Task Replace_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("REPLACE INTO npcs (id) VALUES (1)")).IsNotNull();
    }

    [Test]
    public async Task Attach_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("ATTACH DATABASE 'x.db' AS other")).IsNotNull();
    }

    [Test]
    public async Task Detach_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("DETACH DATABASE other")).IsNotNull();
    }

    [Test]
    public async Task Vacuum_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("VACUUM")).IsNotNull();
    }

    [Test]
    public async Task MultiStatement_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("SELECT 1; DROP TABLE npcs")).IsNotNull();
    }

    [Test]
    public async Task MultiStatement_WithOnlySelects_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("SELECT 1; SELECT 2")).IsNotNull();
    }

    [Test]
    public async Task SemicolonInsideStringLiteral_IsAllowed()
    {
        await Assert.That(SqlGuard.Validate("SELECT 'a;b' AS v")).IsNull();
    }

    [Test]
    public async Task CommentObfuscatedKeyword_IsRejected()
    {
        // Keyword hidden inside a comment must still be caught.
        await Assert.That(SqlGuard.Validate("SELECT 1 /* DROP TABLE npcs */")).IsNotNull();
    }

    [Test]
    public async Task LineCommentObfuscatedSemicolon_IsRejected()
    {
        // Semicolon hidden in a line comment must still be caught.
        await Assert.That(SqlGuard.Validate("SELECT 1 -- ; DROP TABLE npcs")).IsNotNull();
    }

    [Test]
    public async Task Pragma_JournalMode_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("PRAGMA journal_mode = WAL")).IsNotNull();
    }

    [Test]
    public async Task Pragma_UserVersion_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("PRAGMA user_version = 1")).IsNotNull();
    }

    [Test]
    public async Task Pragma_UnknownName_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("PRAGMA bogus_pragma")).IsNotNull();
    }

    [Test]
    public async Task EmptyStatement_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("   ")).IsNotNull();
    }

    [Test]
    public async Task NullStatement_IsRejected()
    {
        await Assert.That(SqlGuard.Validate(null!)).IsNotNull();
    }

    [Test]
    public async Task CaseInsensitiveKeyword_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("delete from npcs")).IsNotNull();
    }

    [Test]
    public async Task KeywordInsideStringLiteral_IsRejected()
    {
        // Strict allow-list: forbidden keywords are rejected anywhere in the
        // statement, including inside string literals (defense in depth).
        await Assert.That(SqlGuard.Validate("SELECT 'DROP TABLE' AS note")).IsNotNull();
    }

    // ------------------------------------- regression: SQL read-side escapes

    [Test]
    public async Task PragmaFunction_WalCheckpoint_IsRejected()
    {
        // Table-valued pragma function with side effects, bypassing the
        // PRAGMA allow-list via SELECT.
        await Assert.That(SqlGuard.Validate("SELECT * FROM pragma_wal_checkpoint")).IsNotNull();
    }

    [Test]
    public async Task PragmaFunction_Optimize_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("SELECT * FROM pragma_optimize")).IsNotNull();
    }

    [Test]
    public async Task PragmaFunction_IntegrityCheck_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("SELECT * FROM pragma_integrity_check")).IsNotNull();
    }

    [Test]
    public async Task PragmaFunction_JournalMode_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("SELECT * FROM pragma_journal_mode")).IsNotNull();
    }

    [Test]
    public async Task PragmaFunction_TableInfo_IsAllowed()
    {
        // Schema-read table-valued pragma function is fine.
        await Assert.That(SqlGuard.Validate("SELECT * FROM pragma_table_info('npcs')")).IsNull();
    }

    [Test]
    public async Task Explain_PragmaMutation_IsRejected()
    {
        // EXPLAIN must not smuggle a mutating PRAGMA past the allow-list.
        await Assert.That(SqlGuard.Validate("EXPLAIN PRAGMA journal_mode = WAL")).IsNotNull();
    }

    [Test]
    public async Task Explain_Select_IsAllowed()
    {
        await Assert.That(SqlGuard.Validate("EXPLAIN SELECT * FROM npcs")).IsNull();
    }

    [Test]
    public async Task Explain_Insert_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("EXPLAIN INSERT INTO npcs (id) VALUES (1)")).IsNotNull();
    }

    [Test]
    public async Task LoadExtension_IsRejected()
    {
        // Arbitrary native code execution.
        await Assert.That(SqlGuard.Validate("SELECT load_extension('/tmp/x.so')")).IsNotNull();
    }

    [Test]
    public async Task Pragma_TrailingGarbage_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("PRAGMA table_info(npcs) garbage")).IsNotNull();
    }

    [Test]
    public async Task Pragma_SecondArgument_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("PRAGMA table_info(npcs, items)")).IsNotNull();
    }

    [Test]
    public async Task Pragma_EmptyArgument_IsRejected()
    {
        await Assert.That(SqlGuard.Validate("PRAGMA table_info()")).IsNotNull();
    }

    [Test]
    public async Task Pragma_UnquotedArgument_IsAllowed()
    {
        await Assert.That(SqlGuard.Validate("PRAGMA table_info(npcs)")).IsNull();
    }
}
