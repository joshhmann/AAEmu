using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Core.Managers.World;

public interface ISphereQuestManager
{
    void AddSphereQuestTrigger(SphereQuestTrigger trigger);
    List<SphereQuest> GetQuestSpheres(uint componentId);
    List<SphereQuestTrigger> GetSphereQuestTriggers();
    void Initialize();
    void Load();
    void RemoveSphereQuestTrigger(SphereQuestTrigger trigger);
    /// <summary>
    /// Snapshot of the global quest-STARTER spheres (the ones whose entry
    /// offers a quest via DoOnEnterQuestStarterSphere → AddQuestFromSphere).
    /// A copy under the same lock the Tick reads — safe for perception
    /// queries (quest discovery) that must not race with Load().
    /// </summary>
    List<SphereQuestStarter> GetQuestStartingSpheres();
}