using System.Numerics;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.NPChar;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.World;
using NLog;

namespace AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Autonomous mount management and high-speed mobility service for playerbots.
/// Manages mount summoning, boarding for long-distance transit, and dismounting for combat/interaction.
/// </summary>
public static class BotMountManager
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    /// <summary>Default mounted travel speed in m/s (standard steed / snowlion / elk base ~10.5 m/s).</summary>
    public const float MountedTravelSpeed = 10.5f;

    /// <summary>Default foot travel speed in m/s.</summary>
    public const float FootTravelSpeed = 5.4f;

    /// <summary>Minimum distance threshold (in meters) to justify mounting up.</summary>
    public const float MountDistanceThreshold = 60.0f;

    /// <summary>
    /// Returns true if the character is currently mounted on an active mate.
    /// </summary>
    public static bool IsMounted(Character character)
    {
        ArgumentNullException.ThrowIfNull(character);
        return character.ParentWorld?.MateManager.GetIsMounted(character.ObjId, out _) != null;
    }

    /// <summary>
    /// Ensures the bot is mounted on an active companion, summoning one if necessary.
    /// </summary>
    public static bool EnsureMounted(GameplayActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var character = actor.Character;
        var world = character.ParentWorld;
        if (world == null)
            return false;

        var mateManager = world.MateManager;
        if (mateManager == null)
            return false;

        // Already mounted
        if (mateManager.GetIsMounted(character.ObjId, out _) != null)
            return true;

        // Check if character already has an active summoned mount
        var activeMates = mateManager.GetActiveMates(character.Id);
        var mate = activeMates.FirstOrDefault();

        // If no active mate exists, spawn the character's steed
        if (mate == null)
        {
            var objId = ObjectIdManager.Instance.GetNextId();
            var tlId = (ushort)TlIdManager.Instance.GetNextId();
            mate = new Mate
            {
                ObjId = objId,
                TlId = tlId,
                OwnerId = character.Id,
                OwnerObjId = character.ObjId,
                Name = $"{character.Name}'s Mount",
                Hp = 100,
                MaxHp = 100,
                Level = character.Level,
                Template = new NpcTemplate { Scale = 1f }
            };
            SetParentWorld(mate, world);
            mate.Transform.Local.SetPosition(character.Transform.World.Position);
            world.AddObject(mate);

            var registry = (Dictionary<uint, List<Mate>>?)typeof(MateManager)
                .GetField("_activeMates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .GetValue(mateManager);
            if (registry != null)
            {
                if (!registry.TryGetValue(character.Id, out var mates))
                    registry[character.Id] = mates = [];
                if (!mates.Contains(mate))
                    mates.Add(mate);
            }
        }

        var mountReq = actor.Mount(mate.ObjId);
        if (mountReq.State == ActorLifecycleState.Completed)
        {
            Logger.Debug("[BotMountManager] Character {Name} successfully mounted steed {MateId}", character.Name, mate.ObjId);
            return true;
        }

        Logger.Warn("[BotMountManager] Character {Name} failed to mount steed {MateId}: {Detail}",
            character.Name, mate.ObjId, mountReq.Detail);
        return false;
    }

    /// <summary>
    /// Ensures the bot is dismounted from any active mount before entering combat or interacting.
    /// </summary>
    public static bool EnsureDismounted(GameplayActor actor)
    {
        ArgumentNullException.ThrowIfNull(actor);
        var character = actor.Character;
        var mateManager = character.ParentWorld?.MateManager;
        if (mateManager == null)
            return true;

        if (mateManager.GetIsMounted(character.ObjId, out _) == null)
            return true; // Already on foot

        var dismountReq = actor.Dismount();
        if (dismountReq.State == ActorLifecycleState.Completed)
        {
            Logger.Debug("[BotMountManager] Character {Name} dismounted for foot action", character.Name);
            return true;
        }

        Logger.Warn("[BotMountManager] Character {Name} failed to dismount: {Detail}", character.Name, dismountReq.Detail);
        return false;
    }

    private static void SetParentWorld(GameObject obj, AAEmu.Game.Models.Game.World.WorldInstance world)
    {
        typeof(GameObject)
            .GetField("_parentWorld", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(obj, world);
        typeof(AAEmu.Game.Models.Game.World.Transform.Transform)
            .GetField("_instanceId", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(obj.Transform, world.Id);
    }
}
