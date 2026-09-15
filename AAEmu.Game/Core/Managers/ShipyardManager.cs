using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Shipyard;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.StaticValues;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Transform;
using AAEmu.Game.Models.Tasks.Shipyard;
using AAEmu.Game.Utils.DB;

using NLog;

namespace AAEmu.Game.Core.Managers;

public class ShipyardManager(ITaskManager taskManager, IObjectIdManager objectIdManager, IShipyardIdManager shipyardIdManager, IWorldManager worldManager, ITaxationsManager taxationsManager, ISkillManager skillManager, IShipyardFrameStore frameStore) : Singleton<ShipyardManager>, IShipyardManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();
    /// <summary>Client-visible step marking a frame whose launch ceremony has started.</summary>
    internal const int LaunchCeremonyStep = 1000;

    public Dictionary<uint, ShipyardsTemplate> _shipyardsTemplate = [];
    private Dictionary<uint, Shipyard> _shipyard = [];
    private List<uint> _removedShipyards = [];

    public void Initialize()
    {
        Logger.Info("Initialising Shipyard Manager...");
        ShipyardTickStart();
    }

    private void ShipyardTickStart()
    {
        Logger.Warn("ShipyardUpdateInfoTick: Started");

        var shipyardTickStartTask = new ShipyardTickTask();
        taskManager.Schedule(shipyardTickStartTask, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    public Shipyard Create(Character owner, ShipyardData shipyardData)
    {
        if (!_shipyardsTemplate.TryGetValue(shipyardData.TemplateId, out var template))
            return null;

        var pos = owner.Transform.CloneAsSpawnPosition();
        pos.X = shipyardData.X;
        pos.Y = shipyardData.Y;
        pos.Z = shipyardData.Z;
        pos.Yaw = shipyardData.zRot;

        var objId = objectIdManager.GetNextId();
        var shipId = shipyardIdManager.GetNextId();
        var shipyard = new Shipyard
        {
            Transform = { InstanceId = owner.ParentWorld.Id }, TemplateId = shipyardData.TemplateId, // duplicate Id
            Id = shipyardData.TemplateId,
            ObjId = objId,
            Template = template,
            Faction = owner.Faction,
            Level = 30
        };
        shipyard.Hp = shipyard.MaxHp;
        shipyard.Name = owner.Name;
        shipyard.ModelId = template.ShipyardSteps[shipyardData.Step].ModelId;
        shipyard.Transform.ApplyWorldSpawnPosition(pos);

        shipyard.ShipyardData = new ShipyardData { Id = shipId, TemplateId = template.Id, X = pos.X, Y = pos.Y,
            Z = pos.Z,
            zRot = pos.Yaw,
            MoneyAmount = 0,
            Actions = shipyardData.Step,
            Type = template.OriginItemId,
            OwnerName = owner.Name,
            Type2 = owner.Id,
            Type3 = owner.Faction.Id,
            Spawned = DateTime.UtcNow,
            ObjId = objId,
            Hp = template.ShipyardSteps[shipyardData.Step].MaxHp * 100,
            Step = shipyardData.Step
        };

        // we will make checks for the availability of money and items to create a shipyard
        // and remove from the inventory items and money necessary for the construction of the shipyard
        if (!RemoveRequiredItems(shipyard))
        {
            owner.SendErrorMessage(ErrorMessageType.NotEnoughItem);
            return null;
        }

        _shipyard.Add(shipId, shipyard);
        shipyard.Spawn();
        PersistShipyard(shipyard);

        return shipyard;
    }

    private bool RemoveRequiredItems(Shipyard shipyard)
    {
        var character = worldManager.GetCharacter(shipyard.ShipyardData.OwnerName);
        var designId = shipyard.Template.OriginItemId;
        var moneyOwed = taxationsManager.Taxations[(uint)shipyard.Template.TaxationId].Tax;

        if (!character.Inventory.CheckItems(SlotType.Inventory, designId, 1))
        {
            character.SendErrorMessage(ErrorMessageType.NotEnoughItem);
            Logger.Error("Not enough item Id={0}", designId);
            return false;
        }

        if (character.Money < moneyOwed)
        {
            character.SendErrorMessage(ErrorMessageType.NotEnoughMoney);
            return false;
        }

        var found = character.Inventory.Bag.GetAllItemsByTemplate(designId, -1, out var foundItems, out _);
        if (!found)
        {
            return false;
        }
        var reagents = skillManager.GetSkillReagentsBySkillId(foundItems[0].Template.UseSkillId);
        var skillProducts = skillManager.GetSkillProductsBySkillId(foundItems[0].Template.UseSkillId);
        if (reagents != null && skillProducts != null)
        {
            if (reagents.Count > 0)
            {
                // first check only
                var enough = true;
                foreach (var reagent in reagents)
                {
                    if (character.Inventory.CheckItems(SlotType.Inventory, reagent.ItemId, reagent.Amount))
                    {
                        continue;
                    }

                    enough = false;
                    Logger.Error("Not enough reagents Id={0}, Amount={1}", reagent.ItemId, reagent.Amount);
                }
                if (!enough)
                {
                    return false;
                }
                foreach (var reagent in reagents)
                {
                    character.Inventory.Bag.ConsumeItem(ItemTaskType.SkillReagents, reagent.ItemId, reagent.Amount, null);
                }
            }
            // maybe not needed
            if (skillProducts.Count > 0)
            {
                foreach (var product in skillProducts)
                {
                    character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.SkillEffectGainItem, product.ItemId, product.Amount);
                }
            }
        }
        else
        {
            Logger.Error("Could not find Reagents/Products for Template[{0}]", foundItems[0].Template.UseSkillId);
            return false;
        }

        character.Inventory.Bag.ConsumeItem(ItemTaskType.Shipyard, designId, 1, null);
        character.SubtractMoney(SlotType.Inventory, (int)moneyOwed, ItemTaskType.Shipyard);

        return true;
    }

    public void RemoveShipyard(Shipyard shipyard)
    {
        var shipId = (uint)shipyard.ShipyardData.Id;
        // Remove Shipyard from Shipyard tables
        _removedShipyards.Add(shipId);
        _shipyard.Remove(shipId);
        DeleteShipyardRow(shipId);
        shipyardIdManager.ReleaseId(shipId);
        objectIdManager.ReleaseId(shipyard.ObjId);
        shipyard.Delete();
    }

    public void ShipyardCompleted(Shipyard shipyard)
    {
        var character = worldManager.GetCharacter(shipyard.ShipyardData.OwnerName);
        var found = character.Inventory.Bag.GetAllItemsByTemplate(shipyard.Template.ItemId, -1, out var foundItems, out _);
        if (found)
        {
            // calculate skillData
            var skillData = (SkillItem)SkillCaster.GetByType(SkillCasterType.Item);
            skillData.ItemId = foundItems[0].Id;
            shipyard.ParentWorld.SlaveManager.Create(character, skillData, true, shipyard.Transform);
        }
        RemoveShipyard(shipyard);
    }

    public void ShipyardCompletedTask(Shipyard shipyard)
    {
        // The Craft-branch finish hit and a later default-branch interaction can
        // both reach this for the same frame — grant the scroll only once.
        if (shipyard.ShipyardData.Step == LaunchCeremonyStep)
            return;

        var character = worldManager.GetCharacter(shipyard.ShipyardData.OwnerName);
        character.Inventory.Bag.AcquireDefaultItem(ItemTaskType.Shipyard, shipyard.Template.ItemId, 1, 0);
        var shipyardCompleteTask = new ShipyardCompleteTask { _shipyard = shipyard };

        shipyard.ShipyardData.Step = LaunchCeremonyStep; // last step, the ceremony of launching the ship
        character.BroadcastPacket(new SCShipyardStatePacket(shipyard.ShipyardData), true);
        PersistShipyard(shipyard);

        var animTime = shipyard.Template.CeremonyAnimTime;
        taskManager.Schedule(shipyardCompleteTask, TimeSpan.FromMilliseconds(animTime));
    }

    public void ShipyardTick()
    {
        foreach (var shipyard in _shipyard)
        {
            UpdateShipyardInfo(shipyard.Value);
        }
    }

    private void UpdateShipyardInfo(Shipyard shipyard)
    {
        var isDecaying = DateTime.UtcNow >= shipyard.ShipyardData.Spawned.AddDays(3);

        SetProtectionBuff(shipyard, isDecaying);
        SetDecayBuff(shipyard, isDecaying);
    }

    private void SetProtectionBuff(Shipyard shipyard, bool isDecay)
    {
        if (!isDecay)
        {
            var duration = shipyard.ShipyardData.Spawned - DateTime.UtcNow;
            var mins = Math.Round(duration.TotalMinutes) * 60000;

            var timeleft = shipyard.Template.TaxDuration + mins;

            if (shipyard.Buffs.CheckBuff((uint)BuffConstants.TaxProtection))
                return;

            var protectionBuffTemplate = skillManager.GetBuffTemplate((uint)BuffConstants.TaxProtection);
            if (protectionBuffTemplate != null)
            {
                var casterObj = new SkillCasterUnit(shipyard.ObjId);
                shipyard.Buffs.AddBuff(new Buff(shipyard, shipyard, casterObj, protectionBuffTemplate, null, DateTime.UtcNow), 0, (int)timeleft);
            }
            else
            {
                Logger.Error("Unable to find Protection Buff template");
            }
        }
        else
        {
            if (shipyard.Buffs.CheckBuff((uint)BuffConstants.TaxProtection))
                shipyard.Buffs.RemoveBuff((uint)BuffConstants.TaxProtection);
        }
    }

    private void SetDecayBuff(Shipyard shipyard, bool isDecay)
    {
        if (isDecay)
        {
            if (shipyard.Buffs.CheckBuff((uint)BuffConstants.Deterioration))
            {
                shipyard.ReduceCurrentHp(shipyard, 7);
                var character = worldManager.GetCharacter(shipyard.ShipyardData.OwnerName);
                character.SendPacket(new SCUnitStatePacket(shipyard));
                character.SendPacket(new SCShipyardStatePacket(shipyard.ShipyardData));

                return;
            }

            var protectionBuffTemplate = skillManager.GetBuffTemplate((uint)BuffConstants.Deterioration);
            if (protectionBuffTemplate != null)
            {
                var casterObj = new SkillCasterUnit(shipyard.ObjId);
                shipyard.Buffs.AddBuff(new Buff(shipyard, shipyard, casterObj, protectionBuffTemplate, null, DateTime.UtcNow));
            }
            else
            {
                Logger.Error("Unable to find Deterioration Debuff template");
            }
        }
        else
        {
            if (shipyard.Buffs.CheckBuff((uint)BuffConstants.Deterioration))
                shipyard.Buffs.RemoveBuff((uint)BuffConstants.Deterioration);
        }
    }

    public void Load()
    {
        Logger.Info("Loading Shipyards...");
        using (var connection = SQLite.CreateConnection())
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM shipyards";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new ShipyardsTemplate
                        {
                            Id = reader.GetUInt32("id"),
                            Name = reader.GetString("name"),
                            MainModelId = reader.GetUInt32("main_model_id"),
                            ItemId = reader.GetUInt32("item_id"),
                            CeremonyAnimTime = reader.GetInt32("ceremony_anim_time"),
                            SpawnOffsetFront = reader.GetFloat("spawn_offset_front"),
                            SpawnOffsetZ = reader.GetFloat("spawn_offset_z"),
                            BuildRadius = reader.GetInt32("build_radius"),
                            TaxDuration = reader.GetInt32("tax_duration", 0),
                            OriginItemId = reader.GetUInt32("origin_item_id", 0),
                            TaxationId = reader.GetInt32("taxation_id")
                        };
                        _shipyardsTemplate.Add(template.Id, template);
                    }
                }
            }
            Logger.Info("Loaded {0} shipyards", _shipyardsTemplate.Count);

            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM shipyard_steps";
                command.Prepare();
                using (var reader = new SQLiteWrapperReader(command.ExecuteReader()))
                {
                    while (reader.Read())
                    {
                        var template = new ShipyardSteps
                        {
                            Id = reader.GetUInt32("id"),
                            ShipyardId = reader.GetUInt32("shipyard_id"),
                            Step = reader.GetInt32("step"),
                            ModelId = reader.GetUInt32("model_id"),
                            SkillId = reader.GetUInt32("skill_id"),
                            NumActions = reader.GetInt32("num_actions"),
                            MaxHp = reader.GetInt32("max_hp")
                        };
                        if (_shipyardsTemplate.TryGetValue(template.ShipyardId, out var value))
                        {
                            value.ShipyardSteps.Add(template.Step, template);
                        }
                    }
                }
            }
        }
        LoadPlacedFrames();
    }

    public Shipyard GetShipyard(uint frameId) => _shipyard.GetValueOrDefault(frameId);

    /// <summary>
    /// Upserts the frame's client-visible build state. Best-effort: on DB failure the
    /// in-memory dict stays authoritative for the session and the next contribution
    /// retries the write, so a transient outage never breaks shipbuilding.
    /// </summary>
    public void PersistShipyard(Shipyard shipyard)
    {
        try
        {
            var data = shipyard.ShipyardData;
            frameStore.Upsert(new ShipyardFrameRow(
                data.Id, data.TemplateId, data.Type2, data.OwnerName ?? string.Empty,
                (uint)data.Type3, data.Step, data.Actions, shipyard.Hp,
                data.X, data.Y, data.Z, data.zRot,
                shipyard.Transform.ZoneId, data.Spawned));
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to persist shipyard frame id={0}", shipyard.ShipyardData?.Id);
        }
    }

    private void DeleteShipyardRow(uint frameId)
    {
        try
        {
            frameStore.Delete(frameId);
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to delete shipyard frame row id={0}", frameId);
        }
    }

    /// <summary>
    /// Rehydrates placed frames from the store into the live dict (no spawn: world
    /// instances do not exist yet when Load runs; SpawnAll spawns them later).
    /// Rows whose template no longer exists are skipped and counted once.
    /// </summary>
    public void LoadPlacedFrames()
    {
        var rows = frameStore.LoadAll() ?? [];
        var restored = 0;
        var skipped = 0;
        foreach (var row in rows)
        {
            if (!_shipyardsTemplate.TryGetValue(row.TemplateId, out var template))
            {
                skipped++;
                continue;
            }
            var (currentStep, numAction) = DecomposeProgress(template, row);
            var shipyard = new Shipyard
            {
                Template = template,
                Level = 30,
                Name = row.OwnerName,
            };
            var faction = FactionManager.PeekInstance?.GetFaction((FactionsEnum)row.FactionId);
            if (faction != null)
                shipyard.Faction = faction;
            shipyard.Hp = row.Hp;
            shipyard.ShipyardData = new ShipyardData
            {
                Id = row.FrameId,
                TemplateId = row.TemplateId,
                X = row.X,
                Y = row.Y,
                Z = row.Z,
                zRot = row.Yaw,
                MoneyAmount = 0,
                Actions = row.Actions,
                Type = template.OriginItemId,
                OwnerName = row.OwnerName,
                Type2 = row.OwnerId,
                Type3 = (FactionsEnum)row.FactionId,
                Spawned = row.Spawned,
                ObjId = 0, // pre-restart ObjIds die with the session; SpawnAll assigns fresh ones
                Hp = row.Hp,
                Step = row.Step
            };
            shipyard.RestoreBuildProgress(currentStep, numAction);
            shipyard.Transform.ApplyWorldSpawnPosition(new WorldSpawnPosition
            {
                X = row.X,
                Y = row.Y,
                Z = row.Z,
                Yaw = row.Yaw,
                ZoneId = row.ZoneId
            });
            _shipyard[(uint)row.FrameId] = shipyard;
            restored++;
        }
        if (skipped > 0)
            Logger.Warn("LoadPlacedFrames: skipped {0} frame row(s) with unknown template", skipped);
        Logger.Info("Loaded {0} placed shipyard frame(s)", restored);
    }

    private static (int CurrentStep, int NumAction) DecomposeProgress(ShipyardsTemplate template, ShipyardFrameRow row)
    {
        // Finished frames (owner-stranded at step count, or ceremony-in-flight at the
        // sentinel) carry no live step. Ceremony rows reload as finished-awaiting-launch
        // WITHOUT re-granting: the scroll grant happens-before the sentinel persist
        // (ShipyardCompletedTask grants first, then sets Step = sentinel, then persists),
        // so a sentinel row proves the grant already happened; the sentinel guards in
        // ShipyardCompletedTask/CraftEffect make any later completion call a no-op, and
        // the frame simply lives out its decay lifetime. (The lost ShipyardCompleteTask
        // ship-spawn resume is follow-up, not this slice.)
        if (row.Step == LaunchCeremonyStep || row.Step == template.ShipyardSteps.Count)
            return (-1, 0);
        if (!template.ShipyardSteps.TryGetValue(row.Step, out var stepTemplate) || stepTemplate.NumActions <= 0)
            return (row.Step, 0);
        var baseAction = 0;
        for (var i = 0; i < row.Step; i++)
            if (template.ShipyardSteps.TryGetValue(i, out var s))
                baseAction += s.NumActions;
        return (row.Step, Math.Clamp(row.Actions - baseAction, 0, stepTemplate.NumActions - 1));
    }

    /// <summary>
    /// Spawns rehydrated frames into the world with fresh session ObjIds.
    /// Called once from SpawnManager next to HousingManager.SpawnAll.
    /// </summary>
    public void SpawnAll(WorldInstance world)
    {
        var spawned = 0;
        foreach (var shipyard in _shipyard.Values)
        {
            if (shipyard.ObjId != 0)
                continue;
            var objId = objectIdManager.GetNextId();
            shipyard.ObjId = objId;
            shipyard.ShipyardData.ObjId = objId;
            if (shipyard.Faction == null)
            {
                var faction = FactionManager.PeekInstance?.GetFaction(shipyard.ShipyardData.Type3);
                if (faction != null)
                    shipyard.Faction = faction;
            }
            shipyard.ParentWorld = world;
            shipyard.Spawn();
            spawned++;
        }
        if (spawned > 0)
            Logger.Info("Spawned {0} restored shipyard frame(s)", spawned);
    }
}
