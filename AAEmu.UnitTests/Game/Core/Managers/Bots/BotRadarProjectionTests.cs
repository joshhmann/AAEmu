using System.Numerics;

using AAEmu.Game.Core.Managers.Bots;
using AAEmu.Game.Models;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.DoodadObj;
using AAEmu.Game.Models.Game.DoodadObj.Templates;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.World;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Perception stack layer 1 (LIVE RADAR) on the REAL headless actor: a
/// <see cref="BotRadarProjection.Project"/> call consumes an existing
/// <c>ActorObservation</c> — never a new scan — and enriches each named objId
/// into one <see cref="RadarEntity"/> row.
///
/// What these tests pin:
///  - the census is the observation's own objId lists (a world object the
///    observation does not name is NOT a row); an objId that no longer resolves
///    keeps its identity-only row (kind + objId + frame time) with every live
///    read null — the "gone from the world" fact is never a dropped or
///    invented row;
///  - per-entity truth is a LIVE read (template/name/level/hp/loot/owner/
///    phase/merchant/despawn) and every failed read is an explicit null, never
///    a fabricated value;
///  - the perception band is a hard DROP at 25 m + tolerance, never a clamp;
///  - every row carries <c>ObservedAtUtc</c> (one stamp per frame);
///  - the projection mutates nothing (world state identical before/after) and
///    is deterministic for the same observation.
/// </summary>
[NotInParallel]
public class BotRadarProjectionTests
{
    private const uint Boar3475 = 3475;
    private const uint ProbeItemTemplateId = 91_411;
    /// <summary>Fixture doodad phase group carrying a func-driven loot func.</summary>
    private const uint LootDoodadGroupId = 91_420;
    private const uint LootDoodadFuncId = 91_421;

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
    public async Task Project_ResolvesNpcRow_WithLiveTemplateAndMerchantGate()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("radar-npc");
        JoinActor(actor, session);
        var npcObjId = SpawnNpc(session, actor, new Vector3(3, 4, 0), templateId: Boar3475, level: 12,
            merchant: true, merchantPackId: 77);

        var radar = BotRadarProjection.Project(actor.Observe(), actor.Character);

        var row = radar.Entities.Single(e => e.ObjId == npcObjId);
        await Assert.That(row.Kind).IsEqualTo(RadarEntityKind.Npc);
        await Assert.That(row.TemplateId).IsEqualTo(Boar3475);
        await Assert.That(row.Name).IsEqualTo("radar-boar");
        await Assert.That(row.Level).IsEqualTo(12);
        await Assert.That(row.Hp).IsEqualTo(100);
        // Flat (XY) distance, deliberately not clamped to the radius.
        await Assert.That(row.DistanceM!.Value).IsEqualTo(5f).Within(0.001f);
        await Assert.That(row.Merchant).IsTrue();
        await Assert.That(row.ShopPackId).IsEqualTo(77u);
        // Doodad-only fields stay unknown on an NPC row.
        await Assert.That(row.PhaseFuncGroupId).IsNull();
        await Assert.That(row.OwnerId).IsNull();
        await Assert.That(row.OwnerType).IsNull();
    }

    [Test]
    public async Task Project_CensusIsTheObservation_WorldObjectsOutsideItAreNotRows()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("radar-census");
        JoinActor(actor, session);
        // Both npcs exist in the world and in the actor's region; only ONE is
        // named by the (hand-built) observation being projected.
        var namedObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);
        var unnamedObjId = SpawnNpc(session, actor, new Vector3(4, 0, 0), templateId: 3476);

        var observation = actor.Observe();
        await Assert.That(observation.NearbyNpcObjIds).Contains(unnamedObjId);

        var restricted = BotRadarProjection.Project(
            new ActorObservation
            {
                ActorId = observation.ActorId,
                Position = observation.Position,
                NearbyNpcObjIds = [namedObjId]
            },
            actor.Character);

        await Assert.That(restricted.Entities.Select(e => e.ObjId)).IsEquivalentTo([namedObjId]);
    }

    [Test]
    public async Task Project_UnresolvableObjId_YieldsIdentityOnlyRow()
    {
        var (actor, _) = GameplayActorTestRig.CreateActor("radar-unresolved");
        JoinActor(actor);
        var observation = actor.Observe();

        var radar = BotRadarProjection.Project(
            new ActorObservation
            {
                ActorId = observation.ActorId,
                Position = observation.Position,
                // Never added to the world: no live facts to join.
                NearbyNpcObjIds = [0x7F_FFF1],
                NearbyDoodadObjIds = [0x7F_FFF2]
            },
            actor.Character);

        // Identity-only rows: the census named them, the world no longer
        // resolves them, and every live read is an explicit null — the
        // "gone from the world" fact, never a dropped or invented row.
        await Assert.That(radar.Entities.Count).IsEqualTo(2);
        await Assert.That(radar.Entities.All(e => e.TemplateId == null
            && e.Name == null && e.Position == null && e.DistanceM == null && e.Hp == null)).IsTrue();
    }

    [Test]
    public async Task Project_NpcBeyondBand_IsDroppedNeverClamped()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("radar-band");
        JoinActor(actor, session);
        var nearObjId = SpawnNpc(session, actor, new Vector3(10, 0, 0), templateId: Boar3475);
        var farObjId = SpawnNpc(session, actor, new Vector3(60, 0, 0), templateId: 3476);
        GameplayActorTestRig.SetPosition(actor, Vector3.Zero);

        // The census is hand-built so it can name the out-of-band object the
        // way a stale observation would.
        var radar = BotRadarProjection.Project(
            new ActorObservation
            {
                ActorId = actor.ActorId,
                Position = Vector3.Zero,
                NearbyNpcObjIds = [nearObjId, farObjId]
            },
            actor.Character);

        await Assert.That(radar.Entities.Select(e => e.ObjId)).IsEquivalentTo([nearObjId]);
        await Assert.That(radar.Entities.All(e => e.DistanceM <= BotRadarProjection.PerceivedRadiusM)).IsTrue();
        await Assert.That(radar.PerceivedRadiusM).IsEqualTo(BotRadarProjection.PerceivedRadiusM);
    }

    [Test]
    public async Task Project_DeadNpcWithLoot_ReportsEngineLootVerdict()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("radar-loot");
        JoinActor(actor, session);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);

        // A live npc's container is empty — false, but a JOINED false.
        var living = BotRadarProjection.Project(actor.Observe(), actor.Character);
        var livingRow = living.Entities.Single(e => e.ObjId == npcObjId);
        await Assert.That(livingRow.Lootable).IsFalse();
        await Assert.That(livingRow.LootContainer).IsEqualTo(0);

        // One container ENTRY (the entry itself carries the item count): the
        // radar reports entries, the same unit ProbeCorpse reads.
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 2));
        npc.Hp = 0;
        var corpse = BotRadarProjection.Project(actor.Observe(), actor.Character);
        var corpseRow = corpse.Entities.Single(e => e.ObjId == npcObjId);
        await Assert.That(corpseRow.Lootable).IsTrue();
        await Assert.That(corpseRow.LootContainer).IsEqualTo(1);

        // Emptied container: joined, authoritative, not unknown.
        GameplayActorTestRig.SeedLootContainer(npc);
        var looted = BotRadarProjection.Project(actor.Observe(), actor.Character);
        var lootedRow = looted.Entities.Single(e => e.ObjId == npcObjId);
        await Assert.That(lootedRow.Lootable).IsFalse();
        await Assert.That(lootedRow.LootContainer).IsEqualTo(0);
    }

    [Test]
    public async Task Project_UnjoinedNpcFacts_StayNull()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("radar-unknown");
        JoinActor(actor, session);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475, withTemplate: false);
        var npc = session.World.GetNpc(npcObjId)!;
        npc.Template = null; // the template read is the join that failed
        npc.Name = null;

        var radar = BotRadarProjection.Project(actor.Observe(), actor.Character);
        var row = radar.Entities.Single(e => e.ObjId == npcObjId);

        await Assert.That(row.Merchant).IsNull();
        await Assert.That(row.ShopPackId).IsNull();
        await Assert.That(row.Level).IsNull();
        await Assert.That(row.Name).IsNull();
        // Identity + the live reads that did join are still reported.
        await Assert.That(row.TemplateId).IsEqualTo(Boar3475);
        await Assert.That(row.Hp).IsEqualTo(100);
    }

    [Test]
    public async Task Project_DoodadRow_CarriesPhaseOwnerAndFuncDrivenLoot()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("radar-doodad");
        JoinActor(actor, session);
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedDoodadLootInteraction(LootDoodadGroupId, LootDoodadFuncId, ProbeItemTemplateId);

        var objId = session.SpawnDoodad(LootDoodadGroupId);
        var doodad = session.World.GetDoodad(objId)!;
        doodad.FuncGroupId = LootDoodadGroupId;
        doodad.Template = new DoodadTemplate
        {
            Id = LootDoodadGroupId,
            FuncGroups = [new DoodadFuncGroups { Id = LootDoodadGroupId, GroupKindId = DoodadFuncGroups.DoodadFuncGroupKind.Normal }]
        };
        doodad.OwnerId = 4242;
        doodad.OwnerObjId = 0x4001;
        doodad.OwnerType = DoodadOwnerType.Housing;
        JoinRegion(session, doodad, new Vector3(1, 1, 0));

        var radar = BotRadarProjection.Project(actor.Observe(), actor.Character);
        var row = radar.Entities.Single(e => e.ObjId == objId);

        await Assert.That(row.Kind).IsEqualTo(RadarEntityKind.Doodad);
        await Assert.That(row.PhaseFuncGroupId).IsEqualTo(LootDoodadGroupId);
        await Assert.That(row.Lootable).IsTrue();
        // Doodad loot is not container-mediated: never claimed as a count.
        await Assert.That(row.LootContainer).IsNull();
        await Assert.That(row.OwnerId).IsEqualTo(4242u);
        await Assert.That(row.OwnerObjId).IsEqualTo(0x4001u);
        await Assert.That(row.OwnerType).IsEqualTo(nameof(DoodadOwnerType.Housing));
        // An NPC-only read is not invented for a doodad.
        await Assert.That(row.Hp).IsNull();
        await Assert.That(row.Level).IsNull();
    }

    [Test]
    public async Task Project_UnownedDoodad_ReportsUnknownOwnerNotZeros()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("radar-unowned");
        JoinActor(actor, session);
        var objId = session.SpawnDoodad(LootDoodadGroupId);
        var doodad = session.World.GetDoodad(objId)!;
        doodad.Template = new DoodadTemplate { Id = LootDoodadGroupId, FuncGroups = [] };
        JoinRegion(session, doodad, new Vector3(1, 0, 0));

        var radar = BotRadarProjection.Project(actor.Observe(), actor.Character);
        var row = radar.Entities.Single(e => e.ObjId == objId);

        await Assert.That(row.OwnerId).IsNull();
        await Assert.That(row.OwnerObjId).IsNull();
        await Assert.That(row.OwnerType).IsNull();
        // An unset phase is UNKNOWN loot state, not a no-loot verdict.
        await Assert.That(row.PhaseFuncGroupId).IsEqualTo(0u);
        await Assert.That(row.Lootable).IsNull();
    }

    [Test]
    public async Task Project_CharacterRow_IsNamed_AndTheObserverIsNeverItsOwnRow()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("radar-self");
        JoinActor(actor, session);
        var (guest, _) = GameplayActorTestRig.CreateActor("radar-guest");
        GameplayActorTestRig.JoinActorWorld(session, guest);
        GameplayActorTestRig.SetPosition(guest, new Vector3(4, 0, 0));
        var region = session.World.GetRegionByPos(new Vector3(4, 0, 0))!;
        region.AddObject(guest.Character);
        guest.Character.Region = region;

        var radar = BotRadarProjection.Project(actor.Observe(), actor.Character);

        await Assert.That(radar.ActorId).IsEqualTo(actor.ActorId);
        var guestRow = radar.Entities.Single(e => e.ObjId == guest.ActorId);
        await Assert.That(guestRow.Kind).IsEqualTo(RadarEntityKind.Character);
        await Assert.That(guestRow.Name).IsEqualTo(guest.Character.Name);
        // A character is a unit: hp is read; doodad/npc-only facts are not.
        await Assert.That(guestRow.Hp).IsEqualTo(100);
        await Assert.That(guestRow.Merchant).IsNull();
        await Assert.That(guestRow.PhaseFuncGroupId).IsNull();
        // The observer is the frame origin, never an entity of its own frame.
        await Assert.That(radar.Entities.Any(e => e.ObjId == actor.ActorId)).IsFalse();
    }

    [Test]
    public async Task Project_StampsEveryRowWithOneFrameTime_AndIsDeterministic()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("radar-stamp");
        JoinActor(actor, session);
        SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);
        SpawnNpc(session, actor, new Vector3(3, 0, 0), templateId: 3476);
        var observation = actor.Observe();

        var first = BotRadarProjection.Project(observation, actor.Character);
        var second = BotRadarProjection.Project(observation, actor.Character);

        await Assert.That(first.Entities.Count).IsEqualTo(2);
        await Assert.That(first.Entities.All(e => e.ObservedAtUtc == first.TakenAtUtc)).IsTrue();
        // Ordered by objId → the rows of two projections of one observation
        // are identical apart from the (live) read time.
        await Assert.That(first.Entities.Select(e => e.ObjId).ToList())
            .IsEquivalentTo(second.Entities.Select(e => e.ObjId).ToList());
        await Assert.That(first.Entities.Select(e => e.ObjId).SequenceEqual(
            first.Entities.Select(e => e.ObjId).OrderBy(id => id))).IsTrue();
    }

    [Test]
    public async Task Project_MutatesNothing()
    {
        var (actor, session) = GameplayActorTestRig.CreateActor("radar-pure");
        JoinActor(actor, session);
        var npcObjId = SpawnNpc(session, actor, new Vector3(2, 0, 0), templateId: Boar3475);
        var npc = session.World.GetNpc(npcObjId)!;
        GameplayActorTestRig.SeedItemTemplate(ProbeItemTemplateId);
        GameplayActorTestRig.SeedLootContainer(npc, (ProbeItemTemplateId, 1));
        npc.Hp = 0;
        var doodadObjId = session.SpawnDoodad(LootDoodadGroupId);
        var doodad = session.World.GetDoodad(doodadObjId)!;
        doodad.Template = new DoodadTemplate { Id = LootDoodadGroupId, FuncGroups = [] };
        JoinRegion(session, doodad, new Vector3(1, 0, 0));

        var worldBefore = session.World.GetAllNpcs().Count + session.World.GetAllDoodads().Count;
        var npcHpBefore = npc.Hp;
        var containerBefore = npc.LootingContainer.Items.Count;
        var phaseBefore = doodad.FuncGroupId;

        _ = BotRadarProjection.Project(actor.Observe(), actor.Character);

        await Assert.That(session.World.GetAllNpcs().Count + session.World.GetAllDoodads().Count).IsEqualTo(worldBefore);
        await Assert.That(npc.Hp).IsEqualTo(npcHpBefore);
        await Assert.That(npc.LootingContainer.Items.Count).IsEqualTo(containerBefore);
        await Assert.That(doodad.FuncGroupId).IsEqualTo(phaseBefore);
    }

    // ------------------------------------------------------------- fixture

    /// <summary>
    /// Joins the actor's character to its region grid at its current position:
    /// CreateActor registers the character with the world, but AddObject alone
    /// never joins the region graph, so Observe's GetAround would see nothing.
    /// </summary>
    private static void JoinActor(GameplayActor actor, HeadlessSession? session = null)
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

    private static uint SpawnNpc(
        HeadlessSession session,
        GameplayActor actor,
        Vector3 position,
        uint templateId,
        byte level = 1,
        bool merchant = false,
        uint merchantPackId = 0,
        bool withTemplate = true)
    {
        var objId = GameplayActorTestRig.SpawnNpc(session, templateId);
        var npc = session.World.GetNpc(objId)!;
        if (withTemplate)
        {
            npc.Template = new NpcTemplate
            {
                Id = templateId,
                Name = "radar-boar",
                Level = level,
                Merchant = merchant,
                MerchantPackId = merchantPackId,
                Scale = 1f
            };
        }

        npc.Name = "radar-boar";
        npc.Hp = 100;
        npc.MaxHp = 100;
        npc.IsVisible = true;
        JoinRegion(session, npc, position);
        return objId;
    }

    private static void JoinRegion(HeadlessSession session, GameObject gameObject, Vector3 position)
    {
        gameObject.Transform.Local.SetPosition(position);
        var region = session.World.GetRegionByPos(position);
        if (region == null)
            return;
        region.AddObject(gameObject);
        if (gameObject is Npc npc)
            npc.Region = region;
        if (gameObject is Doodad doodad)
            doodad.Region = region;
    }
}
