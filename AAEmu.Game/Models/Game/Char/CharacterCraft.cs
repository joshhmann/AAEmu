using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game.Crafts;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Tasks.Skills;
using NLog;

namespace AAEmu.Game.Models.Game.Char;

public class CharacterCraft(Character owner)
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    private int Count { get; set; }
    private Craft CurrentCraft { get; set; }
    /// <summary>
    /// Crafter doodad Id
    /// </summary>
    private uint DoodadId { get; set; }
    private int ConsumeLaborPower { get; set; }
    private Character Owner => owner;
    public bool IsCrafting { get; set; }

    /// <summary>
    /// True while a craft queue is active — either a step is currently casting (IsCrafting)
    /// or a continuation is scheduled for a remaining Count. Guards CSExecuteCraft against
    /// overwriting CurrentCraft/Count/DoodadId mid-queue (a scheduled CraftTask would then
    /// clobber the new craft's state when it fires).
    /// </summary>
    public bool IsCraftQueueActive => IsCrafting || Count > 0;

    /// <summary>
    /// Default interaction range for craft skills that do not define a max range (MaxRange == 0).
    /// </summary>
    private const float DefaultCraftRange = 5f;

    public void Craft(Craft craft, int count, uint doodadId)
    {
        CurrentCraft = craft;
        Count = count;
        DoodadId = doodadId;

        // check if you are equipped with a backpack or glider
        if (!Owner.Inventory.CanReplaceGliderInBackpackSlot())
        {
            // TODO verified
            Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.BackpackOccupied, 0, false);
            CancelCraft();
            return;
        }

        // Canonical 1.2: "10레벨 미만은 특산품 제작/판매 불가" — trade packs require level 10 to craft.
        if (craft.ResultsInBackpack && Owner.Level < AppConfiguration.Instance.Specialty.MinLevelToCraftSell)
        {
            Owner.SendErrorMessage(ErrorMessageType.LevelLowToUse);
            CancelCraft();
            return;
        }

        // Check if we have enough materials (in the bag only — bank/equipment materials are NOT consumable for crafting)
        var hasMaterials = craft.CraftMaterials.Count == 0 || craft.CraftMaterials.All(craftMaterial => Owner.Inventory.GetItemsCount(SlotType.Inventory, craftMaterial.ItemId) >= craftMaterial.Amount);
        if (!hasMaterials)
        {
            // TODO not verified
            Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.NotEnoughRequiredItem, 0, false);
            CancelCraft();
            return;
        }

        var skillTemplate = SkillManager.Instance.GetSkillTemplate(craft.SkillId);
        if (skillTemplate == null)
        {
            // Crafts with a missing skill template (data gap) must not NRE the server
            Logger.Warn("Craft {0} references missing skill {1} for {2}", craft.Id, craft.SkillId, Owner.Name);
            Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.CraftInvalidCraftType, 0, false);
            CancelCraft();
            return;
        }

        var doodad = Owner.ParentWorld?.GetDoodad(doodadId);

        // Workstation integrity: a craft whose skill targets a doodad MUST be executed at a
        // real, in-range workbench. This closes the objId=0 / bogus-objId bypass where
        // hasPermission stayed true and the recipe ran with no workstation at all.
        if (skillTemplate.TargetType == SkillTargetType.Doodad)
        {
            if (doodad == null)
            {
                Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.CraftPermissionDeny, 0, false);
                CancelCraft();
                return;
            }

            // Recipe requires a specific workbench template
            if (craft.ReqDoodadId > 0 && doodad.TemplateId != craft.ReqDoodadId)
            {
                Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.CraftPermissionDeny, 0, false);
                CancelCraft();
                return;
            }

            // Range check — the client normally enforces this, but the skill's own range
            // check exempts doodads, so the server must enforce it explicitly.
            var maxRange = skillTemplate.MaxRange > 0 ? skillTemplate.MaxRange : DefaultCraftRange;
            if (Owner.GetDistanceTo(doodad, true) > maxRange)
            {
                Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.CraftPermissionDeny, 0, false);
                CancelCraft();
                return;
            }
        }

        // Check if we have permission to actually use the doodad (mostly sanity check since the client already checks this before you can craft)
        var hasPermission = true;
        if (doodad != null && doodad.FuncPermission != DoodadFuncPermission.Any && Owner != null)
        {
            switch (doodad.FuncPermission)
            {
                case DoodadFuncPermission.Any:
                case DoodadFuncPermission.Permission1:
                case DoodadFuncPermission.Permission2:
                case DoodadFuncPermission.Permission4:
                case DoodadFuncPermission.OwnerRaidMembers:
                    break;
                case DoodadFuncPermission.OwnerOnly:
                    // OwnerOnly workstations are usable by the owner only (or the house owner)
                    hasPermission = IsDoodadOwner(doodad);
                    break;
                case DoodadFuncPermission.SameAccount:
                    var ownerAccountId = GetDoodadOwnerAccountId(doodad);
                    hasPermission = ownerAccountId > 0 && ownerAccountId == Owner.AccountId;
                    break;
                case DoodadFuncPermission.ZoneResidents:
                    hasPermission = false;
                    var zoneGroup = ZoneManager.Instance.GetZoneByKey(doodad.Transform.ZoneId)?.GroupId ?? 0;
                    var playerHouses = new Dictionary<uint, House>();
                    if (HousingManager.Instance.GetByAccountId(playerHouses, Owner.AccountId) > 0)
                    {
                        foreach (var (_, playerHouse) in playerHouses)
                        {
                            var houseZoneGroup = ZoneManager.Instance.GetZoneByKey(playerHouse.Transform.ZoneId)?.GroupId ?? 0;
                            if (houseZoneGroup == zoneGroup)
                            {
                                hasPermission = true;
                                break;
                            }
                        }
                    }

                    break;
                default:
                    // Unknown permission values must not crash the server; fail closed.
                    Logger.Warn("Crafting: unknown doodad permission {0} on doodad template {1} (objId {2}) for {3} — denied",
                        doodad.FuncPermission, doodad.TemplateId, doodad.ObjId, Owner.Name);
                    hasPermission = false;
                    break;
            }

            Owner.SendDebugMessage($"Crafting using @DOODAD_NAME({doodad.TemplateId}) - {doodad.TemplateId} (objId: {doodad.ObjId}) with current permission {doodad.FuncPermission} = {hasPermission}");
        }

        if (!hasPermission)
        {
            // TODO not verified
            Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.CraftPermissionDeny, 0, false);
            CancelCraft();
            return;
        }

        IsCrafting = true;

        var caster = SkillCaster.GetByType(SkillCasterType.Unit);
        caster.ObjId = Owner.ObjId;

        var target = SkillCastTarget.GetByType(SkillCastTargetType.Doodad);
        target.ObjId = doodadId;

        var skill = new Skill(skillTemplate);
        ConsumeLaborPower = skill.GetLaborCost(Owner);
        var speedMultiplier = 1f;
        if (skill.Template.ActabilityGroupId > 0)
        {
            var currentActAbilityPoints = 0u;
            var actAbility = owner.Actability.Actabilities.GetValueOrDefault((uint)skill.Template.ActabilityGroupId);
            if (actAbility != null)
            {
                speedMultiplier *= actAbility.GetProductionTimeMultiplier();
                currentActAbilityPoints = (uint)actAbility.Point;
            }

            // Check bonus from housing
            var house = HousingManager.Instance.GetHouseAtLocation(owner.Transform.World.Position.X, owner.Transform.World.Position.Y);
            // We don't bother to check house permission here as you can't use the workbench if you don't have permission anyway
            if (house != null)
                currentActAbilityPoints += HousingManager.Instance.GetActAbilityBonusFromHouse(skill.Template.ActabilityGroupId, house);

            // Validate skill level
            if (craft.ActabilityLimit > currentActAbilityPoints)
            {
                Owner.SendErrorMessage(ErrorMessageType.ActabilityNotEnoughPoint, (uint)skill.Template.ActabilityGroupId);
                CancelCraft();
                // This breaks the craft panel, but shouldn't happen if the client is in sync with the server
                return;
            }
        }
        /*
        if (craft.AcId > 0)
        {
            var actAbilityId = CharacterManager.Instance.GetActabilityIdByCategoryId(craft.AcId);
            if (actAbilityId > 0)
            {
                var actAbility = owner.Actability.Actabilities.GetValueOrDefault(actAbilityId);
                if (actAbility != null)
                {
                    speedMultiplier *= actAbility.GetProductionTimeMultiplier();
                }
            }
        }
        */
        skill.CastTimeMultiplier = speedMultiplier;
        skill.Use(Owner, caster, target, null, false, out _);
    }

    /// <summary>
    /// Returns true when the doodad is owned by the crafting character (or by a house owned by them).
    /// </summary>
    private bool IsDoodadOwner(Doodad doodad)
    {
        var ownerCharacter = doodad.GetOwnerCharacter();
        return ownerCharacter != null && ownerCharacter.Id == Owner.Id;
    }

    /// <summary>
    /// Returns the account id that owns the doodad, or 0 when the owner cannot be resolved
    /// (e.g. owner character not online). Never throws for offline owners.
    /// </summary>
    private uint GetDoodadOwnerAccountId(Doodad doodad)
    {
        switch (doodad.OwnerType)
        {
            case DoodadOwnerType.Character:
                return WorldManager.Instance.GetCharacterById(doodad.OwnerId)?.AccountId ?? 0;
            case DoodadOwnerType.Housing:
                return HousingManager.Instance.GetHouseById(doodad.OwnerDbId)?.AccountId ?? 0;
            default:
                return 0;
        }
    }

    /// <summary>
    /// Called when a single craft step completes (via CraftEffect). Returns true when the step
    /// succeeded (products granted + materials consumed). On failure the caller should cancel
    /// the skill so its EndSkill does not burn labor for a step that produced nothing.
    /// </summary>
    public bool EndCraft()
    {
        Count--;
        IsCrafting = false;

        if (CurrentCraft == null)
        {
            CancelCraft();
            return false;
        }

        // Labor check — uses the same adjusted cost as Skill.EndSkill, so a step that passes
        // this check will always have its labor actually consumed, and a step that fails here
        // will not have labor burned by the skill ending afterwards.
        if (Owner.LaborPower < ConsumeLaborPower)
        {
            Owner.SendDebugMessage("|cFFFFFF00[Craft] Not enough Labor Powers for crafting! Performing a fictitious crafting step...|r");
            // TODO not verified
            Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.NotEnoughLaborPower, 0, false);
            CraftOrCancel();
            return false;
        }

        // Check we still have the materials (they might have been spent elsewhere between
        // Craft() and this step's completion)
        var hasMaterials = CurrentCraft.CraftMaterials.Count == 0 || CurrentCraft.CraftMaterials.All(craftMaterial => Owner.Inventory.GetItemsCount(SlotType.Inventory, craftMaterial.ItemId) >= craftMaterial.Amount);
        if (!hasMaterials)
        {
            Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.NotEnoughRequiredItem, 0, false);
            CancelCraft();
            return false;
        }

        // Check if all products can physically fit (stack-aware, matches AcquireDefaultItem math)
        if (!CanGrantAllProducts(CurrentCraft))
        {
            // TODO not verified
            Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.NotEnoughSpace, 0, false);
            CancelCraft();
            return false;
        }

        /*
        // "Proper" Grade inheritance referencing the compact flags, doesn't work for 1.2
        // Find the material that determines the grade for inheritance
        byte inheritedGrade = 0;
        var mainGradeMaterial = CurrentCraft.CraftMaterials.FirstOrDefault(m => m.MainGrade);
        Owner.SendDebugMessage($"Looking for main grade material. Found: {mainGradeMaterial?.ItemId ?? 0}");
        if (mainGradeMaterial != null)
        {
            // Search Bag container for the material  
            Item foundMaterial = null;
            if (Owner.Inventory.Bag.GetAllItemsByTemplate(mainGradeMaterial.ItemId, -1, out var items, out _))
            {
                if (items.Count > 0)
                {
                    foundMaterial = items[0];
                }
            }

            if (foundMaterial != null)
            {
                inheritedGrade = foundMaterial.Grade;
                Owner.SendDebugMessage($"Found material {mainGradeMaterial.ItemId} with grade {inheritedGrade}");
            }
            else
            {
                Owner.SendDebugMessage($"Could not find material {mainGradeMaterial.ItemId} in any container");
            }
        }

        foreach (var product in CurrentCraft.CraftProducts)
        {
            // Determine the grade to use for this product  
            int gradeToUse = -1; // Default grade

            if (product.UseGrade)
            {
                // If UseGrade is true, inherit from main grade material and roll for free regrade  
                gradeToUse = FreeRegrade((int)inheritedGrade);
                Owner.SendDebugMessage($"Product {product.ItemId} will use inherited grade {gradeToUse}");
            }
            else if (product.ItemGradeId > 0)
            {
                // If ItemGradeId is specified, use that grade  
                gradeToUse = (int)product.ItemGradeId;
                Owner.SendDebugMessage($"Product {product.ItemId} will use fixed grade {gradeToUse}");
            }
            else
            {
                Owner.SendDebugMessage($"Product will use default grade: {gradeToUse}");
            }

            // Check if template allows grade changes  
            var template = ItemManager.Instance.GetTemplate(product.ItemId);
            if (template != null)
            {
                Owner.SendDebugMessage($"Product template {product.ItemId} - FixedGrade: {template.FixedGrade}, Gradable: {template.Gradable}");
            }

            // Check if we're crafting a trade pack, if so, try to remove currently equipped backpack slot
            if (ItemManager.Instance.IsAutoEquipTradePack(product.ItemId) == false)
            {
                Owner.Inventory.Bag.AcquireDefaultItem(ItemTaskType.CraftActSaved, product.ItemId, product.Amount, gradeToUse, Owner.Id);
            }
            else
            {
                if (!Owner.Inventory.TryEquipNewBackPack(ItemTaskType.CraftPickupProduct, product.ItemId, product.Amount, gradeToUse, Owner.Id))
                {
                    Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.BackpackOccupied, 0, false);
                    CancelCraft();
                    return;
                }
            }
        }
        */

        // "Improper" Heuristic Grade inheritance to be used for 1.2 only, due to unset flags in compact.
        byte inheritedGrade = 0;
        Item gradeMaterial = null;
        // Find equipment materials that could provide grade  
        // Search for the first equipment material in the craft  
        foreach (var material in CurrentCraft.CraftMaterials)
        {
            var template = ItemManager.Instance.GetTemplate(material.ItemId);
            if (template is EquipItemTemplate) // Check if material is equipment  
            {
                // Search bag container for this material
                if (Owner.Inventory.Bag.GetAllItemsByTemplate(material.ItemId, -1, out var items, out _))
                {
                    if (items.Count > 0)
                    {
                        gradeMaterial = items[0];
                        inheritedGrade = gradeMaterial.Grade;
                        break;
                    }
                }
            }
        }

        // Consume the materials BEFORE granting any product. A product is only ever granted
        // after its full material cost has been paid — this closes the duplicate-item vector
        // by construction (the old code granted first and ignored both return values).
        foreach (var material in CurrentCraft.CraftMaterials)
        {
            var consumed = Owner.Inventory.Bag.ConsumeItem(ItemTaskType.CraftActSaved, material.ItemId, material.Amount, null);
            if (consumed < material.Amount)
            {
                Logger.Warn("Craft {0} step for {1} consumed {2}/{3} of material {4} — cancelling queue",
                    CurrentCraft.Id, Owner.Name, consumed, material.Amount, material.ItemId);
                Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.NotEnoughRequiredItem, 0, false);
                CancelCraft();
                return false;
            }
        }

        foreach (var product in CurrentCraft.CraftProducts)
        {
            // Chance-based products (rate < 100): roll per craft step. A failed roll grants
            // nothing for this product row — the craft itself still succeeded, so materials
            // and labor are consumed as usual (canonical 1.2 behavior for rate-gated items).
            if (product.Rate < 100 && Random.Shared.Next(100) >= product.Rate)
            {
                Owner.SendDebugMessage($"Craft {CurrentCraft.Id} product {product.ItemId} chance roll failed ({product.Rate}%) — no item granted");
                continue;
            }

            // Determine if this product should inherit grade  
            var productTemplate = ItemManager.Instance.GetTemplate(product.ItemId);
            var gradeToUse = -1;

            // Explicitly fixed grades win over the material-inheritance heuristic
            if (product.ItemGradeId > 0)
            {
                // Use specified grade if set  
                gradeToUse = (int)product.ItemGradeId;
            }
            else if (gradeMaterial != null)
            {
                // If we found an equipment material, inherit grade and roll for free regrade
                gradeToUse = FreeRegrade(inheritedGrade);
            }

            // Check if template allows grade changes  
            if (productTemplate != null)
            {
                Owner.SendDebugMessage($"Product template {product.ItemId} - FixedGrade: {productTemplate.FixedGrade}, Gradable: {productTemplate.Gradable}");
            }

            if (ItemManager.Instance.IsAutoEquipTradePack(product.ItemId) == false)
            {
                if (!Owner.Inventory.Bag.AcquireDefaultItem(ItemTaskType.CraftActSaved, product.ItemId, product.Amount, gradeToUse, Owner.Id))
                {
                    // Capacity was pre-checked in CanGrantAllProducts — this is a data/race anomaly
                    Logger.Error("Craft {0} step for {1} failed to grant product {2} after capacity check — cancelling queue", CurrentCraft.Id, Owner.Name, product.ItemId);
                    Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.NotEnoughSpace, 0, false);
                    CancelCraft();
                    return false;
                }
            }
            else
            {
                if (!Owner.Inventory.TryEquipNewBackPack(ItemTaskType.CraftPickupProduct, product.ItemId, product.Amount, gradeToUse, Owner.Id))
                {
                    Owner.SendErrorMessage(ErrorMessageType.CraftCantActAnyMore, ErrorMessageType.BackpackOccupied, 0, false);
                    CancelCraft();
                    return false;
                }
            }
        }

        //Owner.Quests.OnCraft(_craft); // TODO added for quest Id=6024
        // инициируем событие
        //Task.Run(() =>
        //{
        //    if (_craft != null)
        //    {
        //        QuestManager.Instance.DoOnCraftEvents(Owner, _craft.Id);
        //    }
        //});
        QuestManager.Instance.DoOnCraftEvents(Owner, CurrentCraft.Id);

        if (Count > 0)
        {
            ScheduleCraft();
            // Owner.SendMessage($"Continue craft: {_craft.Id} for {_count} more times TaskId: {newCraft.Id}, cooldown: {nextCraftDelay.TotalMilliseconds}ms");
        }
        else
        {
            CancelCraft();
        }

        return true;
    }

    /// <summary>
    /// Stack-aware capacity check for all products of the craft. Matches the math used by
    /// ItemContainer.AcquireDefaultItemEx (existing stacks' headroom + free slots × MaxCount).
    /// Backpack products additionally require the backpack slot to be replaceable.
    /// </summary>
    private bool CanGrantAllProducts(Craft craft)
    {
        foreach (var product in craft.CraftProducts)
        {
            var template = ItemManager.Instance.GetTemplate(product.ItemId);
            if (template == null)
                return false; // Invalid template — cannot grant

            if (ItemManager.Instance.IsAutoEquipTradePack(product.ItemId))
            {
                // Trade pack goes to the backpack slot: the current backpack (if any) must be
                // replaceable, and the takeoff needs at least one free bag slot.
                if (!Owner.Inventory.CanReplaceGliderInBackpackSlot())
                    return false;
                if (Owner.Inventory.FreeSlotCount(SlotType.Inventory) < 1)
                    return false;
                continue;
            }

            // Free space for this template: headroom in existing stacks + free slots × MaxCount
            var totalUnits = 0;
            var totalCapacity = 0;
            if (Owner.Inventory.Bag.GetAllItemsByTemplate(product.ItemId, -1, out var existingItems, out var existingUnits))
            {
                totalUnits = existingUnits;
                totalCapacity = existingItems.Count * template.MaxCount;
            }

            var freeSpace = totalCapacity - totalUnits + Owner.Inventory.Bag.FreeSlotCount * template.MaxCount;
            if (product.Amount > freeSpace)
                return false;
        }

        return true;
    }

    private void CraftOrCancel()
    {
        if (Count > 0)
        {
            ScheduleCraft();
        }
        else
            CancelCraft();
    }

    private void ScheduleCraft()
    {
        var newCraft = new CraftTask(Owner, CurrentCraft.Id, DoodadId, Count);
        var skillTemplate = SkillManager.Instance.GetSkillTemplate(CurrentCraft.SkillId);
        var timeToGlobalCooldown = Owner.GlobalCooldown - DateTime.UtcNow;
        var nextCraftDelay = timeToGlobalCooldown.TotalMilliseconds > skillTemplate.CooldownTime
            ? timeToGlobalCooldown
            : TimeSpan.FromMilliseconds(skillTemplate.CooldownTime);
        TaskManager.Instance.Schedule(newCraft, nextCraftDelay);
    }

    /// <summary>
    /// Stops the current craft queue and interrupts the related skill.
    /// </summary>
    public void CancelCraft()
    {
        IsCrafting = false;
        CurrentCraft = null;
        Count = 0;
        DoodadId = 0;

        // Also cancel the related skill ? I don't think this really does anything for crafts, but can't hurt I guess
        if (Owner != null)
        {
            if (Owner.SkillTask != null)
                Owner.SkillTask.Skill.Cancelled = true;
            Owner.InterruptSkills();
        }

        // Might want to send a packet here, I think there is a packet when crafting fails. Not sure yet.
    }

    /// <summary>
    ///Roll for chance of free regrade, Use when inheriting grade only.
    /// Uses a magic number for the chance based on user statistics, replace if/when actual data tables are found.
    ///</summary>
    private static int FreeRegrade(int baseGrade)
    {
        var grade = baseGrade;
        var maxGrade = ItemManager.MaxGradeValue;
        //Check grade is not already max
        if (grade != maxGrade)
        {
            //5% chance
            var luckyRoll = Random.Shared.Next(0, 20);
            if (luckyRoll < 1)
            {
                grade++;
            }
        }
        return grade;
    }
}
