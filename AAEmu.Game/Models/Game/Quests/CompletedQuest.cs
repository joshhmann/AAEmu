using System.Collections;

namespace AAEmu.Game.Models.Game.Quests;

public class CompletedQuest
{
    // BUG-014: block id = questId / 64 is uint arithmetic — a ushort key wrapped
    // for quest ids >= 4,194,304 (e.g. 8000004 -> 125000 -> 59464), making
    // ResetQuests recompute the wrong quest id and never clear the completed bit.
    public uint Id { get; set; }
    public BitArray Body { get; set; }

    public CompletedQuest()
    {
    }

    public CompletedQuest(uint id)
    {
        Id = id;
        Body = new BitArray(64);
    }
}