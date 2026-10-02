using System.Collections.Concurrent;
using System.Reflection;

using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Quests;
using AAEmu.Game.Models.Game.Units;
using MySql.Data.MySqlClient;

namespace AAEmu.UnitTests.Game.Quests.Scenario;

/// <summary>
/// t_90c0d0d1: CharacterQuests.Save NRE on a null completed-block entry — the
/// disconnect save aborts BEFORE the active-quest REPLACE loop and quest rows
/// are lost (observed live: game.log 12:21:08, CharacterQuests.Save:590 NRE via
/// Character.Save:2782, runtime 711181bc0).
///
/// Mechanism: CompletedQuests is a plain Dictionary mutated by SetCompletedQuestFlag
/// (check-then-act Add). A concurrent Add during Save's live enumeration
/// (CompletedQuests.Values) can yield a NULL entry (the enumerator reads an empty
/// slot of a resized entries array), and `quest.Id` NREs. Both Add sites
/// (SetCompletedQuestFlag:394, Load:515) write non-null values, so the null can
/// only be a mutation-during-enumeration artifact — exactly the class this rig
/// freezes with a deterministic injected null.
///
/// Rig design (hermetic — NO live MySQL): the NRE fires at parameter binding
/// (quest.Id) BEFORE any ExecuteNonQuery, so a never-opened MySqlConnection is
/// sufficient to reproduce the crash. Pass-after asserts the null entry is
/// skipped (WARN) and the save proceeds to the remaining blocks / the
/// active-quest REPLACE loop.
///
/// Fail-before (pre-fix): both tests throw NullReferenceException at quest.Id —
/// the rig is RED. Pass-after (fix): GREEN.
/// </summary>
[NotInParallel]
public class CharacterQuestsSaveNullEntryRigTests
{
    private const string HermeticConnStr =
        "Server=127.0.0.1;Port=3306;Database=aaemu_game;Uid=root;Pwd=hermetic;";

    private static CharacterQuests BuildQuests()
    {
        var character = new Character(new UnitCustomModelParams());
        return new CharacterQuests(character);
    }

    /// <summary>Injects a null entry into the private CompletedQuests dictionary —
    /// the mutation-during-enumeration artifact the race yields.</summary>
    private static void InjectNullCompletedBlock(CharacterQuests quests, uint blockId)
    {
        var field = typeof(CharacterQuests).GetField(
            "<CompletedQuests>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        var dict = (ConcurrentDictionary<uint, CompletedQuest>)field!.GetValue(quests)!;
        dict.TryAdd(blockId, null!);
    }

    /// <summary>Injects a null entry into the public ActiveQuests dictionary — the
    /// same artifact class in the active-quest REPLACE loop.</summary>
    private static void InjectNullActiveQuest(CharacterQuests quests, uint questId)
    {
        quests.ActiveQuests.TryAdd(questId, null!);
    }

    /// <summary>
    /// Core repro: the completed-block save loop must survive a null entry —
    /// skip it with a WARN and still reach the active-quest REPLACE loop.
    /// Pre-fix: NRE at quest.Id (save aborts, active loop never runs).
    /// Post-fix: null skipped, empty active loop → Save returns cleanly.
    /// </summary>
    [Test]
    public async Task Save_NullCompletedBlockEntry_Skipped_SaveCompletes()
    {
        var quests = BuildQuests();
        InjectNullCompletedBlock(quests, blockId: 5);

        using var connection = new MySqlConnection(HermeticConnStr);
        // Hermetic: connection is never opened. Pre-fix code throws NRE at quest.Id
        // before any DB call; post-fix code must skip the null and return cleanly
        // (no completed blocks left to write, no active quests).
        quests.Save(connection, transaction: null);
        await Task.CompletedTask;
    }

    /// <summary>
    /// The null entry must not swallow the REAL completed blocks around it:
    /// after the skip, the loop must still process every non-null block. The
    /// observable proof is the THROW SITE, not the exception type.
    ///
    /// <para>
    /// ORDERING (this revision corrects the previous one). The old version
    /// injected the null at block 5 and created the "real" block with
    /// <c>SetCompletedQuestFlag(64, true)</c> — which lands in block **1** —
    /// then asserted the thrown exception was "not something else". Two things
    /// were wrong with that:
    /// </para>
    /// <list type="number">
    /// <item>
    /// <c>CompletedQuests</c> is a <c>ConcurrentDictionary</c>, so
    /// <c>Values</c> enumerates in BUCKET order, not insertion order. Block 1
    /// sits in an earlier bucket than block 5, so the loop NREd on the REAL
    /// block and never reached the null at all. The documented "null inserted
    /// first ⇒ deterministic fail-before" was false: this test stayed GREEN
    /// with both null-skip guards deleted.
    /// </item>
    /// <item>
    /// <c>SetCompletedQuestFlag(64, true)</c> was dead setup. The save loop
    /// reads <c>quest.Id</c>, never <c>quest.Body</c> — the completion bit it
    /// set is not what the REPLACE writes, so it pinned nothing about the
    /// loop continuing.
    /// </item>
    /// </list>
    ///
    /// <para>
    /// What is pinned now, on the REAL hermetic-connection error (the throw
    /// every non-null block provokes, since the connection is never opened):
    /// pre-fix the loop dies on the FIRST enumerated block; post-fix the null
    /// is skipped and the real block is still reached and written.
    /// </para>
    /// </summary>
    [Test]
    public async Task Save_NullCompletedBlockEntry_RealBlockStillWritten()
    {
        var quests = BuildQuests();
        // Bucket 0 enumerates ahead of bucket 8, so the null is seen FIRST:
        // pre-fix the loop NREs at quest.Id before it ever reaches the real
        // block — a real fail-before that does not depend on ConcurrentDictionary
        // enumeration-order luck.
        InjectNullCompletedBlock(quests, blockId: 0);
        var realBlock = quests.SetCompletedQuestFlag(8 * 64, true);

        // The hermetic connection is never opened, so the real block's REPLACE
        // throws MySqlException at ExecuteNonQuery — the observable that proves
        // the loop walked past the null onto live data.
        using var connection = new MySqlConnection(HermeticConnStr);
        Exception thrown = null;
        try
        {
            quests.Save(connection, transaction: null);
        }
        catch (Exception ex)
        {
            thrown = ex;
        }
        // The precise observable: the hermetic connection is never opened, so
        // ExecuteNonQuery on a real block throws InvalidOperationException
        // ("Connection not open"). Reaching THAT throw proves the loop walked
        // past the null onto live data. Pre-fix it is a NullReferenceException
        // at quest.Id instead, so the two are cleanly distinguished — the
        // previous `is null or InvalidOperationException or MySqlException`
        // accepted both the success path and the crash.
        await Assert.That(thrown).IsTypeOf<InvalidOperationException>()
            .Because("the null at block 0 must be skipped and the loop must reach the real " +
                     $"block {realBlock.Id}, whose REPLACE throws on the unopened connection");
    }

    /// <summary>
    /// Same defect class in the ACTIVE-quest REPLACE loop: a null active entry
    /// must be skipped, not dereferenced.
    /// Pre-fix: NRE at quest.Id in the active loop.
    /// Post-fix: null skipped → Save returns cleanly.
    /// </summary>
    [Test]
    public async Task Save_NullActiveQuestEntry_Skipped_SaveCompletes()
    {
        var quests = BuildQuests();
        InjectNullActiveQuest(quests, questId: 251);

        using var connection = new MySqlConnection(HermeticConnStr);
        quests.Save(connection, transaction: null);
        await Task.CompletedTask;
    }
}
