using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Models.Game.Units.Route;
using AAEmu.Game.Models.Game.World;

using NLog;

namespace AAEmu.Game.Models.Game.NPChar;

public class NpcSpawnerNpc : Spawner<Npc>
{
    // ReSharper disable once InconsistentNaming
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>
    /// NpcSpawnerTemplateId
    /// </summary>
    public uint NpcSpawnerTemplateId { get; init; }
    /// <summary>
    /// NpcTemplateId
    /// </summary>
    public uint MemberId { get; set; }
    /// <summary>
    /// MemberType should be "Npc" here
    /// </summary>
    public string MemberType { get; set; }
    /// <summary>
    /// Spawn priority weight
    /// </summary>
    public float Weight { get; init; }

    public NpcSpawnerNpc()
    {
        //
    }

    /// <summary>
    /// Creates a new instance of NpcSpawnerNpcs with a Spawner template id (npc_spanwers)
    /// </summary>
    /// <param name="spawnerTemplateId"></param>
    public NpcSpawnerNpc(uint spawnerTemplateId)
    {
        NpcSpawnerTemplateId = spawnerTemplateId;
    }

    public NpcSpawnerNpc(uint spawnerTemplateId, uint npcTemplateId)
    {
        NpcSpawnerTemplateId = spawnerTemplateId;
        MemberId = npcTemplateId;
        MemberType = "Npc";
    }

    /// <summary>
    /// Spawns Npcs from a NpcSpawner
    /// </summary>
    /// <param name="npcSpawner"></param>
    /// <param name="ownerId"></param>
    /// <returns>List of newly spawned NPCs</returns>
    /// <exception cref="InvalidOperationException"></exception>
    public List<Npc> Spawn(NpcSpawner npcSpawner, uint ownerId = 0)
    {
        switch (MemberType)
        {
            case "Npc":
                return SpawnNpc(npcSpawner, ownerId);
            case "NpcGroup":
                return SpawnNpcGroup(npcSpawner, ownerId);
            default:
                throw new InvalidOperationException($"Tried spawning an unsupported line from NpcSpawnerNpc - Id: {Id}");
        }
    }

    /// <summary>
    /// Internal Spawn Npc function for Spawn
    /// </summary>
    /// <param name="npcSpawner"></param>
    /// <param name="ownerId"></param>
    /// <returns></returns>
    private List<Npc> SpawnNpc(NpcSpawner npcSpawner, uint ownerId = 0)
    {
        var npcs = new List<Npc>();
        var npc = NpcManager.Instance.Create(npcSpawner.ParentWorld, 0, MemberId);
        if (npc == null)
        {
            Logger.Warn($"Npc {MemberId}, from spawner Id {npcSpawner.Id} not exist at db. Spawner Position: {npcSpawner.Position}");
            return null;
        }

        npc.ParentWorld = npcSpawner.ParentWorld;
        npc.OwnerId = ownerId;

        npc.RegisterNpcEvents();

        Logger.Trace($"Spawn npc templateId {MemberId} objId {npc.ObjId} from spawnerId {NpcSpawnerTemplateId} at Position: {npcSpawner.Position}");

        // PB-005 remedy A: ground units at least ClampSeverityM (0.5 m) above sampled ground,
        // or past NegativeClampSeverityM (-0.1 m) below it, are snapped to that ground height
        // (the old rule only "corrected" deltas < 1 m, which let frozen-z source data reach
        // clients verbatim). Fly/swim and whitelisted units keep their spawner z; sub-threshold
        // offsets keep source z, and negative offsets keep source z for cave/interior dwellers,
        // because raw terrain cannot distinguish roads/decks, caves, and interiors.
        var pos = npcSpawner.Position.AsPositionVector();
        float? hintFloor = NpcGroundingPolicy.TryGetDeckFloor(pos.X, pos.Y, pos.Z, out var deckFloor) ? deckFloor : null;
        var groundZ = hintFloor ?? npcSpawner.ParentWorld.Template.GeoData.GetHeight(pos);
        switch (NpcGroundingPolicy.ResolveSpawnZ(MemberId, npc.CanFly, npcSpawner.Position.Z, groundZ, out var resolvedZ))
        {
            case NpcGroundingPolicy.SpawnGroundingAction.ClampedToGround:
                NpcGroundingPolicy.ReportClamp(MemberId, npcSpawner.Position.X, npcSpawner.Position.Y, npcSpawner.Position.Z, resolvedZ);
                npcSpawner.Position.Z = resolvedZ;
                break;
        }

        npc.Transform.ApplyWorldSpawnPosition(npcSpawner.Position);
        if (npc.Transform == null)
        {
            Logger.Error($"Can't spawn npc {MemberId} from spawnerId {NpcSpawnerTemplateId}. Transform is null.");
            return null;
        }

        npc.Transform.InstanceId = npc.Transform.InstanceId;

        if (npc.Ai != null)
        {
            npc.Ai.HomePosition = npc.Transform.World.Position;
            npc.Ai.IdlePosition = npc.Ai.HomePosition;
            npc.Ai.GoToSpawn();
        }

        npc.Spawner = npcSpawner;
        npc.Spawner.RespawnTime = (int)Random.Shared.Next(npc.Spawner.Template.SpawnDelayMin, npc.Spawner.Template.SpawnDelayMax);
        ApplyRespawnFloor(npc);
        npc.Spawn();

        var world = WorldManager.Instance.GetWorld(npc.Transform.InstanceId);
        world.Events.OnUnitSpawn(world, new OnUnitSpawnArgs { Npc = npc });
        npc.Simulation = new Simulation(npc);

        if (npc.Ai != null && !string.IsNullOrWhiteSpace(npcSpawner.FollowPath))
        {
            if (!npc.Ai.LoadAiPathPoints(npcSpawner.FollowPath, false))
                Logger.Warn($"Failed to load {npcSpawner.FollowPath} for NPC {npc.TemplateId} ({npc.ObjId})");
        }

        npcs.Add(npc);
        return npcs;
    }

    /// <summary>
    /// Internal Spawn NpcGroup function for Spawn
    /// </summary>
    /// <param name="npcSpawner"></param>
    /// <param name="ownerId"></param>
    /// <returns></returns>
    private List<Npc> SpawnNpcGroup(NpcSpawner npcSpawner, uint ownerId = 0)
    {
        return SpawnNpc(npcSpawner, ownerId);
    }

    /// <summary>
    /// Raises RespawnTime to the configured floor when the spawner's authored delays are data
    /// placeholders (both SpawnDelayMin and SpawnDelayMax at or below the threshold, e.g. the
    /// 11,773 compact.sqlite3 rows set to 10s/10s). Spawners with a genuinely authored delay
    /// (bosses, rares) return early and are left alone.
    /// </summary>
    public static void ApplyRespawnFloor(Npc npc)
    {
        var cfg = AppConfiguration.Instance.World;
        if (cfg == null || npc?.Spawner?.Template == null)
            return;

        var template = npc.Spawner.Template;
        if (template.SpawnDelayMax > cfg.NpcRespawnPlaceholderThreshold ||
            template.SpawnDelayMin > cfg.NpcRespawnPlaceholderThreshold)
            return; // Real authored timer, do not touch

        var floor = cfg.NpcRespawnMinSeconds;
        if (cfg.NpcRespawnMinSecondsElite > 0 &&
            npc.Template != null &&
            (int)npc.Template.NpcGradeId >= cfg.NpcRespawnEliteMinGrade)
        {
            floor = cfg.NpcRespawnMinSecondsElite;
        }

        if (floor > 0 && npc.Spawner.RespawnTime < floor)
            npc.Spawner.RespawnTime = floor;
    }
}
