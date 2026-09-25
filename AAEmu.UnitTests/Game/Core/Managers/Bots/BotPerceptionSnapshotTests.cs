using System.Numerics;

using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;
using AAEmu.Game.Models.Game.World.Zones;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Perception stack layer 2 (PERCEPTION SNAPSHOT) on the REAL headless actor:
/// one radar frame becomes one typed, lifecycle-stamped frame.
///
/// What these tests pin:
///  - the radar frame is the whole census (capture neither scans nor mutates);
///  - the by-kind views partition the entities and the observer is never one
///    of its own character rows;
///  - every unjoined fact stays UNKNOWN — Merchant/QuestGiver/Owner/loot never
///    read as a fabricated false when the join failed;
///  - a joined false IS reported as false (an empty container on a live npc);
///  - FirstSeen carries across frames while LastSeen advances, and the
///    lifecycle follows the real corpse/loot transitions of the engine state.
/// </summary>
[NotInParallel]
public class BotPerceptionSnapshotTests
{
    private const uint Boar3475 = 3475;
    private const uint ProbeItemTemplateId = 91_411;
    private const uint LootDoodadGroupId = 91_430;
    private const uint LootDoodadFuncId = 91_431;

    [Before(Test)]
    public void SetUp()
    {
        ExecutionBoundary.SetExecutionThreadForTest(Environment.CurrentManagedThreadId);
        AppConfiguration.Instance.World ??= new WorldConfig();
        GameplayActorTestRig.Seed();
        QuestBehavior.ClearCorpseMemory();
        QuestBehavior.ClearLootMemory();
    }

    [Test]
    public async Task Capture_ClassifiesByKind_AndTheObserverIsNeverItsOwnCharacterRow()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-kinds");
        JoinActor(actor);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);
        var doodadObjId = SpawnDoodad(session, actor, new Vector3(3, 0, 0));
        var (guest, _) = GameplayActorTestRig.CreateActor("snap-guest");
        GameplayActorTestRig.JoinActorWorld(session, guest);
        JoinRegion(session, guest.Character, new Vector3(4, 0, 0));

        var snapshot = Capture(actor);

        await Assert.That(snapshot.Entities.Select(e => e.ObjId).ToList())
            .IsEquivalentTo([npcObjId, doodadObjId, guest.ActorId]);
        await Assert.That(snapshot.Npcs.Select(e => e.ObjId)).IsEquivalentTo([npcObjId]);
        await Assert.That(snapshot.Doodads.Select(e => e.ObjId)).IsEquivalentTo([doodadObjId]);
        await Assert.That(snapshot.Characters.Select(e => e.ObjId)).IsEquivalentTo([guest.ActorId]);
        await Assert.That(snapshot.Entities.Any(e => e.ObjId == actor.ActorId)).IsFalse();
        await Assert.That(snapshot.SelfPosition).IsEqualTo(actor.Observe().Position);
        // One frame time stamps the frame and every row in it.
        await Assert.That(snapshot.Entities.All(e => e.LastSeen == snapshot.ObservedAt)).IsTrue();
    }

    [Test]
    public async Task Capture_Npc_JoinsNameLevelHpMaxHpAndHostility()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-npc");
        JoinActor(actor);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475, level: 9);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.MaxHp = 137;

        var snapshot = Capture(actor);
        var row = snapshot.Npcs.Single(e => e.ObjId == npcObjId);

        await Assert.That(row.TemplateId).IsEqualTo(Boar3475);
        await Assert.That(row.Name).IsEqualTo("snap-boar");
        await Assert.That(row.Npc!.Level).IsEqualTo(9);
        await Assert.That(row.Npc.Hp).IsEqualTo(100);
        // MaxHp is a live npc read (template formula driven in the real
        // engine), so it is asserted against the same live source.
        await Assert.That(row.Npc.MaxHp).IsEqualTo(npc.MaxHp);
        await Assert.That(row.Npc.Alive).IsTrue();
        // A factionless fixture npc reads hostile (the engine's own rule).
        await Assert.That(row.Npc.Hostile).IsTrue();
        await Assert.That(row.IsTargetable).IsTrue();
    }

    [Test]
    public async Task Capture_UnjoinedNpcFacts_StayUnknown()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-unknown");
        JoinActor(actor);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = null; // template join failed
        npc.Name = null;
        npc.OwnerId = 0;     // no owner
        npc.Spawner = null;  // no spawner

        var snapshot = Capture(actor);
        var row = snapshot.Npcs.Single(e => e.ObjId == npcObjId);

        await Assert.That(row.Npc!.Merchant).IsNull();
        await Assert.That(row.Npc.Level).IsNull();
        await Assert.That(row.Name).IsNull();
        await Assert.That(row.Npc.Owner).IsNull();
        await Assert.That(row.Npc.SpawnerId).IsNull();
    }

    [Test]
    public async Task Capture_MerchantGate_AndQuestGiverGate_AreLiveJoins()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-gates");
        JoinActor(actor);
        // Merchant WITH a shop pack is the live shopping gate.
        var merchantObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: 90_801, merchant: true, merchantPackId: 55);
        // Merchant flag without a pack is NOT shoppable — a joined false.
        var halfMerchantObjId = SpawnNpc(session, actor, new Vector3(3, 0, 0), templateId: 90_802, merchant: true);
        // A template that offers a quest through the real accept-act index.
        var offererObjId = SpawnNpc(session, actor, new Vector3(4, 0, 0), templateId: 90_803);
        GameplayActorTestRig.SeedQuestOffer(90_810, 90_811, 90_803, level: 1);

        var snapshot = Capture(actor);
        var byId = snapshot.Npcs.ToDictionary(e => e.ObjId);

        await Assert.That(byId[merchantObjId].Npc!.Merchant).IsTrue();
        await Assert.That(byId[halfMerchantObjId].Npc!.Merchant).IsFalse();
        await Assert.That(byId[offererObjId].Npc!.QuestGiver).IsTrue();
        // A template with no accept act is a joined false, not unknown.
        await Assert.That(byId[merchantObjId].Npc!.QuestGiver).IsFalse();
    }

    [Test]
    public async Task Capture_LootFacts_UnknownOnlyWhenUnjoined()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-loot");
        JoinActor(actor);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);

        // Live npc, empty container: joined, authoritative empty.
        var live = Capture(actor).Npcs.Single(e => e.ObjId == npcObjId);
        await Assert.That(live.Corpse).IsNull();
        await Assert.That(live.Lifecycle).IsEqualTo(PerceptionLifecycle.Spawned);

        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 3));
        npc.Hp = 0;
        var corpse = Capture(actor).Npcs.Single(e => e.ObjId == npcObjId);
        await Assert.That(corpse.Corpse!.OriginalNpcTemplate).IsEqualTo(Boar3475);
        await Assert.That(corpse.Corpse.Lootable).IsTrue();
        await Assert.That(corpse.Corpse.LootCount).IsEqualTo(1); // container entries
        await Assert.That(corpse.Corpse.Looted).IsFalse();
    }

    [Test]
    public async Task Capture_Doodad_JoinsPhaseOwnerAndContainerSemantics()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-doodad");
        JoinActor(actor);
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedDoodadLootInteraction(LootDoodadGroupId, LootDoodadFuncId, ProbeItemTemplateId);

        var objId = SpawnDoodad(session, actor, new Vector3(2, 0, 0));
        var doodad = session.World.GetDoodad(objId)!;
        doodad.FuncGroupId = LootDoodadGroupId;
        doodad.Template = new DoodadTemplate
        {
            Id = LootDoodadGroupId,
            FuncGroups = [new DoodadFuncGroups { Id = LootDoodadGroupId, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Normal }]
        };
        doodad.OwnerId = 77;
        doodad.OwnerObjId = 0x5A5A;
        doodad.OwnerType = DoodadOwnerType.Housing;

        var row = Capture(actor).Doodads.Single(e => e.ObjId == objId);

        await Assert.That(row.Doodad!.FuncGroupId).IsEqualTo(LootDoodadGroupId);
        await Assert.That(row.Doodad.Phase).IsEqualTo(nameof(DoodadFuncGroups.DoodadFuncGroupKind.Normal));
        await Assert.That(row.Doodad.OwnerId).IsEqualTo(77u);
        await Assert.That(row.Doodad.OwnerObjId).IsEqualTo(0x5A5Au);
        await Assert.That(row.Doodad.OwnerType).IsEqualTo(nameof(DoodadOwnerType.Housing));
        await Assert.That(row.Doodad.Interactable).IsTrue();
        // A loot-phase doodad is not a harvest-phase doodad: the live harvest
        // predicate says so, and a non-coffer never claims a container.
        await Assert.That(row.Doodad.Harvestable).IsFalse();
        await Assert.That(row.Doodad.Container).IsNull();
        await Assert.That(row.IsTargetable).IsFalse();
    }

    [Test]
    public async Task Capture_Place_ReadsTheObserverZone_NotAnEntityZone()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-place");
        JoinActor(actor);

        // The observer's zone (0 in the rig world — pin one) and a DIFFERENT
        // zone for a doodad in the same region.
        var zoneManager = EnsureZoneManager();
        var zones = (Dictionary<uint, Zone>)GameplayActorTestRig.GetField(zoneManager, "_zones");
        zones[90_901] = new Zone { Id = 90_901, ZoneKey = 90_901, Name = "snap-zone" };
        zones[90_902] = new Zone { Id = 90_902, ZoneKey = 90_902, Name = "other-zone" };
        actor.Character.Transform.ZoneId = 90_901;

        var doodadObjId = SpawnDoodad(session, actor, new Vector3(1, 0, 0));
        session.World.GetDoodad(doodadObjId)!.Transform.ZoneId = 90_902;

        var snapshot = Capture(actor);

        await Assert.That(snapshot.Place).IsEqualTo("snap-zone");
    }

    /// <summary>
    /// The rig world carries zone key 0 and the base rig seeds no ZoneManager
    /// (only the housing surface does, missing-only): provide the unloaded
    /// instance + empty tables the same way, so the observer-zone read has
    /// somewhere to join from in any suite ordering.
    /// </summary>
    private static ZoneManager EnsureZoneManager()
    {
        if (ZoneManager.PeekInstance is { } existing)
            return existing;

        var manager = new ZoneManager(WorldManager.Instance);
        GameplayActorTestRig.SeedSingleton(typeof(Singleton<ZoneManager>), manager);
        var seeded = ZoneManager.Instance;
        foreach (var (field, value) in new (string, object)[]
                 {
                     ("_zones", new Dictionary<uint, Zone>()),
                     ("_groups", new Dictionary<uint, ZoneGroup>()),
                     ("_zoneIdToKey", new Dictionary<uint, uint>()),
                     ("_climateElem", new Dictionary<uint, ZoneClimateElem>())
                 })
        {
            SetPrivateField(seeded, field, value);
        }

        return seeded;
    }

    private static void SetPrivateField(object target, string name, object value)
        => target.GetType()
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(target, value);

    [Test]
    public async Task Capture_CarriesFirstSeenForward_AndAdvancesLastSeen()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-history");
        JoinActor(actor);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);

        var first = Capture(actor);
        var second = Capture(actor, first);

        var before = first.Npcs.Single(e => e.ObjId == npcObjId);
        var after = second.Npcs.Single(e => e.ObjId == npcObjId);

        // The first observation is the spawn evidence; continuity keeps it.
        await Assert.That(before.Lifecycle).IsEqualTo(PerceptionLifecycle.Spawned);
        await Assert.That(after.Lifecycle).IsEqualTo(PerceptionLifecycle.Live);
        await Assert.That(after.FirstSeen).IsEqualTo(before.FirstSeen);
        await Assert.That(after.LastSeen >= before.LastSeen).IsTrue();
    }

    [Test]
    public async Task Capture_AfterDeathWithoutLoot_IsDeadNotCorpse_AndNeverCorpseLooted()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-dead");
        JoinActor(actor);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;

        var live = Capture(actor);
        npc.Hp = 0; // killed with an empty container — nothing to loot

        var dead = Capture(actor, live);
        var row = dead.Npcs.Single(e => e.ObjId == npcObjId);
        await Assert.That(row.Lifecycle).IsEqualTo(PerceptionLifecycle.Dead);
        await Assert.That(row.Corpse!.Looted).IsTrue(); // joined: empty
        await Assert.That(row.Corpse.Lootable).IsFalse();

        // Frame 3: still empty and never observed as a lootable corpse — the
        // stack must not claim it was looted.
        var stillDead = Capture(actor, dead);
        await Assert.That(stillDead.Npcs.Single(e => e.ObjId == npcObjId).Lifecycle)
            .IsEqualTo(PerceptionLifecycle.Dead);
    }

    [Test]
    public async Task Capture_DeathWithLoot_ThenEmptied_IsCorpseThenCorpseLooted()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-corpse");
        JoinActor(actor);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));

        var live = Capture(actor);
        npc.Hp = 0;

        var corpse = Capture(actor, live);
        await Assert.That(corpse.Npcs.Single(e => e.ObjId == npcObjId).Lifecycle)
            .IsEqualTo(PerceptionLifecycle.Corpse);
        await Assert.That(corpse.Corpses.Select(e => e.ObjId)).IsEquivalentTo([npcObjId]);

        GameplayActorTestRig.SeedLootContainer(npc); // taken
        var looted = Capture(actor, corpse);
        await Assert.That(looted.Npcs.Single(e => e.ObjId == npcObjId).Lifecycle)
            .IsEqualTo(PerceptionLifecycle.CorpseLooted);
    }

    [Test]
    public async Task Capture_MutatesNothing()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("snap-pure");
        JoinActor(actor);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        SpawnDoodad(session, actor, new Vector3(3, 0, 0));

        var objectsBefore = session.World.GetAllNpcs().Count + session.World.GetAllDoodads().Count;
        var hpBefore = npc.Hp;

        _ = Capture(actor);

        await Assert.That(session.World.GetAllNpcs().Count + session.World.GetAllDoodads().Count).IsEqualTo(objectsBefore);
        await Assert.That(npc.Hp).IsEqualTo(hpBefore);
    }

    // ------------------------------------------------------------- fixture

    /// <summary>
    /// Joins the actor's character to its region grid at its current position:
    /// CreateActor registers the character with the world, but AddObject alone
    /// never joins the region graph, so Observe's GetAround would see nothing.
    /// </summary>
    private static void JoinActor(GameplayActor actor)
    {
        var character = actor.Character;
        var world = character.ParentWorld
            ?? throw new InvalidOperationException("rig actor has no world");
        var region = world.GetRegionByPos(character.Transform.World.Position);
        if (region == null)
            return;
        region.AddObject(character);
        character.Region = region;
    }

    private static BotPerceptionSnapshot.Snapshot Capture(GameplayActor actor, BotPerceptionSnapshot.Snapshot? previous = null)
        => BotPerceptionSnapshot.Capture(BotRadarProjection.Project(actor.Observe(), actor.Character), actor.Character, previous);

    private static uint SpawnNpc(
        HeadlessSession session,
        GameplayActor actor,
        Vector3 position,
        uint templateId,
        byte level = 1,
        bool merchant = false,
        uint merchantPackId = 0)
    {
        var objId = GameplayActorTestRig.SpawnNpc(session, templateId);
        var npc = session.World.GetNpc(objId)!;
        npc.Template = new NpcTemplate
        {
            Id = templateId,
            Name = "snap-boar",
            Level = level,
            Merchant = merchant,
            MerchantPackId = merchantPackId,
            Scale = 1f
        };
        npc.Name = "snap-boar";
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        JoinRegion(session, npc, position);
        return objId;
    }

    private static uint SpawnDoodad(HeadlessSession session, GameplayActor actor, Vector3 position)
    {
        var objId = session.SpawnDoodad(LootDoodadGroupId);
        var doodad = session.World.GetDoodad(objId)!;
        doodad.Template = new DoodadTemplate { Id = LootDoodadGroupId, FuncGroups = [] };
        JoinRegion(session, doodad, position);
        return objId;
    }

    private static void JoinRegion(HeadlessSession session, GameObject gameObject, Vector3 position)
    {
        gameObject.Transform.Local.SetPosition(position);
        var region = session.World.GetRegionByPos(position);
        if (region == null)
            return;
        region.AddObject(gameObject);
        switch (gameObject)
        {
            case Npc npc:
                npc.Region = region;
                break;
            case Doodad doodad:
                doodad.Region = region;
                break;
        }
    }
}
