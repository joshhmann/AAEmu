using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Static;
using AAEmu.Game.Models.Game.Items;
using AAEmu.Game.Models.Game.Items.Actions;
using AAEmu.Game.Models.Game.Items.Templates;
using AAEmu.Game.Models.Game.Skills.Effects;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Utils;
using NLog;

namespace AAEmu.Game.Core.Managers.World;

/// <summary>
/// Trade-pack → vehicle cargo loading — the REAL gameplay path behind the
/// IGameplayActor.LoadPackOntoVehicle contract action (Phase 2 prerequisite,
/// t_a7756a00). Retail 1.2 snap-to-cargo-point behavior, canonical data:
///
///  - A vehicle's cargo capacity is defined by its slave_doodad_bindings
///    rows whose doodad is a pack-storage box ("등짐 보관 상자" — doodads
///    3446 / 4893(견본), model prefab interaction.xml/container.empty).
///    Farm Wagon 4 (points 9-12), Farm Hauler 6 (9-14), Farm Cart 2 (9-10),
///    Merchant Schooner 20 (Box0-19), small sailboats 4 (Box0-3), tanks 2.
///    See <see cref="IsPackStorageBoxDoodad"/>.
///  - A loaded pack is an ATTACHED DOODAD on the slave (Slave.AttachedDoodads)
///    with ItemId/ItemTemplateId set — exactly what Slave.DestroyAttachedItems
///    drops back to the floor on slave death. The doodad's local transform is
///    snapped to the model's cargo attach point via
///    SlaveManager.ApplyAttachPointLocation (the same snap the binding spawn
///    uses), and ParentObjId/AttachPoint serialize to clients in the doodad
///    stream so the pack renders attached at the cargo point.
///  - The pack item leaves the character's Backpack equipment slot into the
///    System container (the same move PutDownBackpackEffect performs); the
///    doodad holds ItemId/ItemTemplateId, so unload/pickup later works
///    through the ordinary RecoverItem path.
///
/// No manual attachment, direct Transform write, GM, reflection or DB
/// shortcut: every state change goes through ordinary engine surfaces
/// (inventory containers, DoodadManager.Create/Spawn, the SlaveManager
/// attach seam, engine broadcast). This service is the shared gameplay path
/// for both the bot contract action and any future client packet handler.
/// </summary>
public static class PackVehicleService
{
    /// <summary>NLog logger for the service.</summary>
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// Interaction range for loading a pack onto a vehicle (the actor's
    /// MaxInteractRange — the same adjacency the retail client enforces).
    /// </summary>
    public const float MaxLoadRange = 25f;

    /// <summary>
    /// Canonical pack-storage-box doodad templates — the cargo-point markers
    /// in slave_doodad_bindings (1.2 data: "등짐 보관 상자" / sample, model
    /// prefab interaction.xml/container.empty). A binding carrying one of
    /// these defines ONE cargo slot at its attach point.
    /// </summary>
    private static readonly HashSet<uint> PackStorageBoxDoodadIds = [3446u, 4893u];

    /// <summary>True when the doodad template is a pack-storage box (cargo slot marker).</summary>
    public static bool IsPackStorageBoxDoodad(uint doodadTemplateId)
        => PackStorageBoxDoodadIds.Contains(doodadTemplateId);

    /// <summary>
    /// The vehicle's cargo points, in template order — one entry per
    /// pack-storage-box binding on the slave template. A vehicle with no
    /// such bindings cannot carry packs.
    /// </summary>
    public static IReadOnlyList<AttachPointKind> GetCargoPoints(Slave slave)
    {
        if (slave?.Template == null)
            return [];

        var points = new List<AttachPointKind>();
        foreach (var binding in slave.Template.DoodadBindings)
        {
            if (IsPackStorageBoxDoodad(binding.DoodadId) && !points.Contains(binding.AttachPointId))
                points.Add(binding.AttachPointId);
        }

        return points;
    }

    /// <summary>
    /// True when the vehicle can carry packs (at least one cargo point).
    /// </summary>
    public static bool IsCargoVehicle(Slave slave)
        => GetCargoPoints(slave).Count > 0;

    /// <summary>
    /// The first cargo point that currently holds no pack — a point is
    /// occupied when an attached doodad with an item link (ItemId &gt; 0)
    /// sits on it. The invisible storage-box doodads (ItemId == 0) that the
    /// binding spawn places on every point do NOT occupy the slot.
    /// </summary>
    public static AttachPointKind? FindFreeCargoPoint(Slave slave)
    {
        foreach (var point in GetCargoPoints(slave))
        {
            var occupied = slave.AttachedDoodads.Any(d =>
                d.AttachPoint == point && d.ItemId > 0);
            if (!occupied)
                return point;
        }

        return null;
    }

    /// <summary>
    /// Outcome of a load attempt — the engine-side vocabulary the caller
    /// (contract action / future packet handler) maps into its own.
    /// </summary>
    public enum PackLoadResult : byte
    {
        Success = 0,
        UnknownSlave = 1,
        DeadSlave = 2,
        OutOfRange = 3,
        NotACargoVehicle = 4,
        CargoFull = 5,
        NoCarriedPack = 6,
        NotATradePack = 7,
        PlacedPackNotFound = 8,
        PlacedPackOutOfRange = 9,
        PlacedPackAlreadyAttached = 10,
        PlacedPackNotRecoverable = 11,
        EngineRefusal = 12
    }

    /// <summary>What the successful load produced (completion proof payload).</summary>
    public sealed record PackLoadData(Doodad Doodad, Item PackItem, AttachPointKind AttachPoint, bool FromPlacedPack);

    /// <summary>
    /// Loads the character's CARRIED trade pack (Backpack equipment slot)
    /// onto a vehicle's first free cargo point. The pack item moves into the
    /// System container and a pack doodad is spawned attached to the slave,
    /// snapped to the model's cargo attach point.
    /// </summary>
    public static PackLoadResult TryLoadCarriedPack(Character character, Slave slave, out PackLoadData? data)
    {
        data = null;
        var preflight = Preflight(character, slave, out data);
        if (preflight != PackLoadResult.Success)
            return preflight;

        // 1. The pack must be carried in the Backpack equipment slot — the
        //    state PackPickup / pack crafting leave it in, and the exact
        //    lookup PutDownBackpackEffect performs. After a successful load
        //    the slot is empty, so a retry finds no pack (engine-true
        //    idempotency backstop).
        var pack = character.Inventory.Equipment.GetItemBySlot((int)EquipmentItemSlot.Backpack);
        if (pack == null)
            return PackLoadResult.NoCarriedPack;
        if (pack is not Backpack || !ItemManager.Instance.IsAutoEquipTradePack(pack.TemplateId))
            return PackLoadResult.NotATradePack;

        // 2. The placed-pack doodad template comes from the pack's put-down
        //    skill effect — the same derivation DestroyAttachedItems uses to
        //    drop packs back to the floor. No skill → the pack cannot be
        //    represented as a world object → refuse.
        var backpackDoodadId = ResolveBackpackDoodadId(pack);
        if (backpackDoodadId == 0)
            return PackLoadResult.NotATradePack;

        // 3. Move the pack into the System container (the same real
        //    container move PutDownBackpackEffect performs for placement).
        //    This is the retry-proof transition: the pack leaves the slot
        //    before any world object is created.
        if (!character.Inventory.SystemContainer.AddOrMoveExistingItem(ItemTaskType.DropBackpack, pack))
            return PackLoadResult.EngineRefusal;

        // 4. Spawn the pack doodad through the normal doodad factory and
        //    snap it onto the free cargo point (fresh doodad: phase init).
        var doodad = CreatePackDoodad(character, backpackDoodadId, pack);
        if (doodad == null)
        {
            // The doodad could not be created — move the pack back so the
            // failure is not a silent item loss.
            character.Inventory.SystemContainer.RemoveItem(ItemTaskType.DropBackpack, pack, true);
            return PackLoadResult.EngineRefusal;
        }

        var result = AttachToFreeCargoPoint(character, slave, doodad, initializePhase: true, out var attachPoint, out data);
        if (result != PackLoadResult.Success)
        {
            // Roll the item back into the backpack slot; the half-spawned
            // doodad is cleaned up through the ordinary Delete path.
            character.Inventory.SystemContainer.RemoveItem(ItemTaskType.DropBackpack, pack, true);
            character.Inventory.Equipment.AddOrMoveExistingItem(ItemTaskType.DropBackpack, pack,
                (int)EquipmentItemSlot.Backpack);
            doodad.Delete();
            data = null;
            return result;
        }

        return PackLoadResult.Success;
    }

    /// <summary>
    /// Loads a PLACED trade-pack doodad (a pack standing in the world,
    /// recoverable via the generic 11361 recover skill) onto a vehicle's
    /// first free cargo point. The doodad re-parents to the slave, snaps to
    /// the cargo attach point and stays the same persistent object — pickup
    /// and drop-to-floor (DestroyAttachedItems) keep working on it.
    /// </summary>
    public static PackLoadResult TryLoadPlacedPack(Character character, Slave slave, Doodad placedPack, out PackLoadData? data)
    {
        data = null;
        var preflight = Preflight(character, slave, out data);
        if (preflight != PackLoadResult.Success)
            return preflight;

        // 1. The doodad must resolve, be in range, hold an item link and be
        //    a recoverable trade pack (the same routing rule
        //    CSLootOpenBagPacket / PackPickup apply — only the generic
        //    world recover skill makes a doodad a trade pack).
        if (placedPack == null || placedPack.ParentWorld != character.ParentWorld)
            return PackLoadResult.PlacedPackNotFound;
        if (MathUtil.CalculateDistance(character.Transform.World.Position, placedPack.Transform.World.Position, false) > MaxLoadRange)
            return PackLoadResult.PlacedPackOutOfRange;
        if (placedPack.ParentObjId != 0 || placedPack.AttachPoint != AttachPointKind.None)
            return PackLoadResult.PlacedPackAlreadyAttached;

        var recoverable = placedPack.CurrentFuncs.Any(func =>
            func.FuncType == "DoodadFuncRecoverItem" && func.SkillId == GameplayActor.GenericRecoverItemSkillId);
        if (!recoverable)
            return PackLoadResult.PlacedPackNotRecoverable;

        var packItem = placedPack.ItemId > 0
            ? ItemManager.Instance.GetItemByItemId(placedPack.ItemId)
            : null;
        if (packItem is not Backpack || !ItemManager.Instance.IsAutoEquipTradePack(placedPack.ItemTemplateId))
            return PackLoadResult.NotATradePack;

        // 2. Re-parent + snap. The doodad is already in the world and has an
        //    active phase (recover funcs) — hide it (region + visibility),
        //    attach it to the slave, then re-show at the snapped position.
        //    Phase initialization is NOT re-run: the placed pack keeps its
        //    current phase, so the recover funcs cannot re-fire during the
        //    move (a re-run of DoodadFuncRecoverItem would re-grant the pack).
        var result = AttachToFreeCargoPoint(character, slave, placedPack, initializePhase: false, out var attachPoint, out data);
        if (result != PackLoadResult.Success)
            return result;

        return PackLoadResult.Success;
    }

    // ---------------------------------------------------------------- internals

    /// <summary>Common vehicle/range/cargo preflight shared by both variants.</summary>
    private static PackLoadResult Preflight(Character character, Slave slave, out PackLoadData? data)
    {
        data = null;
        if (character?.ParentWorld == null || character.Inventory == null)
            return PackLoadResult.EngineRefusal;
        if (slave == null || slave.ParentWorld != character.ParentWorld)
            return PackLoadResult.UnknownSlave;
        if (slave.Hp <= 0 || slave.Despawn != DateTime.MinValue)
            return PackLoadResult.DeadSlave;
        if (MathUtil.CalculateDistance(character.Transform.World.Position, slave.Transform.World.Position, false) > MaxLoadRange)
            return PackLoadResult.OutOfRange;
        if (!IsCargoVehicle(slave))
            return PackLoadResult.NotACargoVehicle;
        if (FindFreeCargoPoint(slave) == null)
            return PackLoadResult.CargoFull;

        return PackLoadResult.Success;
    }

    /// <summary>Resolves the placed-pack doodad template from the pack's put-down skill effect.</summary>
    private static uint ResolveBackpackDoodadId(Item pack)
    {
        var packTemplate = pack.Template;
        if (packTemplate == null || packTemplate.UseSkillId == 0)
            return 0;

        var skillTemplate = SkillManager.Instance.GetSkillTemplate(packTemplate.UseSkillId);
        if (skillTemplate == null)
            return 0;

        foreach (var skillEffect in skillTemplate.Effects)
        {
            if (skillEffect.Template is PutDownBackpackEffect putDown)
                return putDown.BackpackDoodadId;
        }

        return 0;
    }

    /// <summary>
    /// Creates the pack doodad through the normal doodad factory, mirroring
    /// PutDownBackpackEffect's object shape (item link, plant time, scale).
    /// The caller decides spawn timing (spawn happens inside the attach).
    /// </summary>
    private static Doodad? CreatePackDoodad(Character character, uint backpackDoodadId, Item pack)
    {
        var world = character.ParentWorld;
        var doodad = DoodadManager.Instance.Create(world, 0, backpackDoodadId, character, true);
        if (doodad == null)
        {
            Logger.Warn("PackVehicleService: pack doodad {0} could not be created", backpackDoodadId);
            return null;
        }

        doodad.ItemId = pack.Id;
        doodad.ItemTemplateId = pack.TemplateId;
        doodad.UccId = pack.UccId;
        doodad.SetScale(1f);
        doodad.PlantTime = DateTime.UtcNow;
        return doodad;
    }

    /// <summary>
    /// The shared attach step: finds the free cargo point, snaps the doodad
    /// onto it through the SlaveManager attach seam (model attach-point
    /// position — the retail snap), registers it as an attached doodad and
    /// (re)shows it to the world. Returns the engine result.
    /// </summary>
    private static PackLoadResult AttachToFreeCargoPoint(Character character, Slave slave, Doodad doodad,
        bool initializePhase, out AttachPointKind attachPoint, out PackLoadData? data)
    {
        attachPoint = AttachPointKind.None;
        data = null;

        var freePoint = FindFreeCargoPoint(slave);
        if (freePoint == null)
            return PackLoadResult.CargoFull;

        var slaveManager = slave.ParentWorld?.SlaveManager;
        if (slaveManager == null)
            return PackLoadResult.EngineRefusal;

        // Hide first (region + visibility removal) so the re-show registers
        // the snapped position. A fresh doodad is not visible yet (and has
        // no transform) — Hide is a no-op then (RemoveVisibleObject is
        // registry-safe and skips null transforms).
        if (doodad.IsVisible && doodad.Transform != null)
            doodad.Hide();

        // The real engine seam: parent to the slave, snap the local
        // transform to the model's cargo attach point, register in
        // AttachedDoodads. All inside SlaveManager so the attach-point
        // knowledge stays with the engine code that owns it.
        slaveManager.AttachDoodadAtPoint(slave, doodad, freePoint.Value);
        if (initializePhase)
            doodad.InitDoodad();
        doodad.Spawn();

        data = new PackLoadData(doodad, doodad.ItemId > 0 ? ItemManager.Instance.GetItemByItemId(doodad.ItemId) : null,
            freePoint.Value, doodad.AttachPoint != AttachPointKind.None && doodad.ParentObjId == slave.ObjId);
        return PackLoadResult.Success;
    }
}
