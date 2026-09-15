using AAEmu.Game.Models.Game.Merchant;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.Game.Core.Managers.UnitManagers;

public interface INpcManager : ILoadable
{
    bool Exist(uint templateId);
    NpcTemplate GetTemplate(uint templateId);
    Dictionary<uint, NpcTemplate> GetAllTemplates();
    MerchantGoods GetGoods(uint id);
    IReadOnlyList<uint> GetNpcInteractionSkills(int interactionSetId);
    uint[] BuildInteractionSkillList(uint defaultOption, int interactionSetId);
    Npc Create(WorldInstance parentWorld, uint objectId, uint templateId);
    void LoadAiParams();
    void BindSkillsToTemplate(uint templateId, List<NpcSkill> skills);
}
