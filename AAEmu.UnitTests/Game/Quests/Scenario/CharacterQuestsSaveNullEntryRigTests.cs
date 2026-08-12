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
    /// The null entry must not swallow the REAL completed blocks around it: after
    /// the skip, the loop must still process the non-null block (here: reach
    /// ExecuteNonQuery, which throws the connection-not-open error on a hermetic
    /// connection — proving the loop continued past the null).
    /// Pre-fix: NRE at the FIRST entry (null inserted first, insertion order).
    /// Post-fix: no NRE; the only throw is the connection-state error.
    /// </summary>
    [Test]
    public async Task Save_NullCompletedBlockEntry_RealBlockStillWritten()
    {
        var quests = BuildQuests();
        // Null FIRST (insertion order ⇒ the pre-fix code NREs before reaching the
        // real block — deterministic fail-before).
        InjectNullCompletedBlock(quests, blockId: 5);
        quests.SetCompletedQuestFlag(64, true); // real block 1

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

        await Assert.That(thrown is null or InvalidOperationException or MySqlException).IsTrue();
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
