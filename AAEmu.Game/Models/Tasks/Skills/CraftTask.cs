using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game.Char;

namespace AAEmu.Game.Models.Tasks.Skills;

public class CraftTask(Character character, uint craftId, uint objId, int count)
    : Task
{
    public override void Execute()
    {
        if (count > 0)
        {
            // _character.SendMessage($"CraftTask: {_craftId}");
            var craft = CraftManager.Instance.GetCraftById(craftId);
            if (craft == null)
            {
                // Craft vanished from the manager (data change) — never crash the task loop
                character?.Craft.CancelCraft();
                return;
            }
            character?.Craft.Craft(craft, count, objId);
        }
    }
}
