using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers.Id;
using AAEmu.Game.Core.Managers.UnitManagers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Bots;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Housing;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Units.Route;
using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Character entry/leave lifecycle — the single shared activation path for
/// human clients and headless bots (ARCHITECTURE_REVIEW deliverable 10,
/// slice 3 / H3). Extracted from CSSelectCharacterPacket.Read(): Load →
/// Connection bind → ObjId → TryAddCharacter → Simulation → buffs/HP/MP
/// restore. The human path is byte-identical to the pre-extraction packet
/// body; the headless path runs the same core minus client packets
/// (Unit.SendPacket is null-safe, so a Connection-less character is a no-op
/// sender, Unit.cs:801-804).
/// </summary>
public interface ICharacterLifecycleService
{
    /// <summary>
    /// Activates a character for a real client connection (the
    /// CSSelectCharacterPacket flow). Full login state: world entry,
    /// connection bind, client-state packets, buffs and HP/MP restore.
    /// </summary>
    void ActivateHuman(GameConnection connection, Character character);

    /// <summary>
    /// Activates a character without any client connection — the bot
    /// embodiment entry. Runs the same core as the human path (Load, ObjId,
    /// world add, buffs, HP/MP restore) but no client packets: the gameplay
    /// engine's sends no-op at the null-safe sink. The character must be a
    /// real, loaded Character record (production provisioning lives in
    /// HeadlessSession, review slice 4).
    /// </summary>
    void ActivateHeadless(Character character, BotContext botContext);

    /// <summary>
    /// Deactivates a character: leave/logout semantics reusing
    /// GameConnection.SaveAndRemoveFromWorld (despawn, drop from the world,
    /// persist). Shared by human disconnect/leave paths and headless
    /// deactivation.
    /// </summary>
    void Deactivate(Character character, CharacterLifecycleReason reason);
}

/// <summary>
/// Why a character left the world. Recorded on deactivation; consumed by
/// lifecycle hooks (playerbot_* dirty-flush on deactivate/shutdown, review
/// slice 7) as they land.
/// </summary>
public enum CharacterLifecycleReason
{
    /// <summary>Graceful leave (character select / exit to lobby).</summary>
    Logout,

    /// <summary>Connection dropped (network close, hard DC).</summary>
    Disconnect,

    /// <summary>Server shutdown.</summary>
    Shutdown,
}

public class CharacterLifecycleService : Singleton<CharacterLifecycleService>, ICharacterLifecycleService
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <inheritdoc />
    public void ActivateHuman(GameConnection connection, Character character)
    {
        EnterWorld(character, connection);
        var houses = connection.Houses.Values.Where(x => x.OwnerId == character.Id);

        connection.ActiveChar = character;
        AssignObjId(character);
        // Add to server pool
        WorldManager.Instance.TryAddCharacter(character);

        CancelOwnedSlaveTask(character);

        character.Simulation = new Simulation(character);

        InitializeHumanClientState(connection, character, houses);

        FinalizeActivationState(character);
    }

    /// <inheritdoc />
    public void ActivateHeadless(Character character, BotContext botContext)
    {
        Logger.Trace($"Headless activation: {botContext.Name} (botId {botContext.BotId})");

        EnterWorld(character, connection: null);

        AssignObjId(character);
        // Add to server pool
        WorldManager.Instance.TryAddCharacter(character);

        CancelOwnedSlaveTask(character);

        FinalizeActivationState(character);
    }

    /// <inheritdoc />
    public void Deactivate(Character character, CharacterLifecycleReason reason)
    {
        // TODO: this needs a rewrite
        if (character == null)
            return;

        Logger.Trace($"Deactivating {character.Name} (id {character.Id}, reason {reason})");

        // Remove Radars
        RadarManager.Instance.UnRegister(character);

        // Cancel all running buff effect tasks before removing the character.
        // The buffs themselves are saved to DB inside SaveDirectlyToDatabase() → Character.Save().
        character.Buffs?.CancelAllEffectTasks();

        // Hide/Despawn the player
        character.Delete();
        // Removed ReleaseId here to try and fix party/raid disconnect and reconnect issues. Replaced with saving the data
        //ObjectIdManager.Instance.ReleaseId(character.ObjId);

        // Also drop the entry from WorldManager._characters. Without this, hard-DC
        // / crash paths leak a ghost reference at that ObjId (LeaveWorldTask does
        // the same TryRemoveCharacter explicitly on graceful logout — we have to
        // mirror it here or the next reconnect will TryAddCharacter on a stale slot
        // and end up with a divergent _characters[id] = OLD vs _baseUnits[id] = NEW,
        // so any later operation on the ghost reference Deletes the live character).
        //
        // Guard with an identity check so the cleanup stays safe if ObjId recycling
        // is ever re-enabled: only remove the slot if _characters still maps this
        // ObjId to OUR character — never evict a freshly-spawned entity that
        // happened to inherit the recycled ObjId.
        if (WorldManager.Instance.GetCharacterByObjId(character.ObjId) == character)
            WorldManager.Instance.TryRemoveCharacter(character.ObjId);

        // Do a manual save here as it's no longer in _characters at this point
        // TODO: might need a better option like saving this transaction for later to be used by the SaveManager
        character.SaveDirectlyToDatabase();
    }

    /// <summary>
    /// Shared entry core (human + headless): force main_world instance, load
    /// the character, bind the connection (null for headless), and despawn any
    /// old owned mates across all world instances. Verbatim order from the
    /// pre-extraction CSSelectCharacterPacket.Read().
    /// </summary>
    private static void EnterWorld(Character character, GameConnection connection)
    {
        // Force player into main_world when coming from character select
        character.Transform.InstanceId = WorldManager.DefaultInstanceId;
        // Despawn any old pets this character might have even before loading it
        character.Load();
        character.Connection = connection;
        // Remove old pets from all world instances
        foreach (var worldInstance in WorldManager.Instance.GetWorlds())
        {
            worldInstance.MateManager.RemoveAndDespawnAllActiveOwnedMates(character);
        }
    }

    /// <summary>
    /// ObjId assignment with reconnect-reuse semantics: a character that
    /// re-enters the world keeps the ObjId it had in its previous session;
    /// otherwise a fresh id is allocated and remembered.
    /// </summary>
    private static void AssignObjId(Character character)
    {
        if (Character.UsedCharacterObjIds.TryGetValue(character.Id, out var oldObjId))
        {
            character.ObjId = oldObjId;
        }
        else
        {
            character.ObjId = ObjectIdManager.Instance.GetNextId();
            Character.UsedCharacterObjIds.TryAdd(character.Id, character.ObjId);
        }
    }

    /// <summary>Aborts the task of disabling vehicles for any slave this character still owns.</summary>
    private static void CancelOwnedSlaveTask(Character character)
    {
        var mySlave = character.ParentWorld.SlaveManager.GetActiveSlaveByOwnerObjId(character.ObjId);
        if (mySlave != null)
        {
            Logger.Warn($"{character.Name}: Abort the task of disabling vehicles");
            mySlave.CancelTokenSource.Cancel();
        }
    }

    /// <summary>
    /// Human-only client-state initialization (the packet block of the
    /// pre-extraction CSSelectCharacterPacket.Read()). Runs after the
    /// character is in the world; sends the full login state the client
    /// expects. Headless activation deliberately skips this.
    /// </summary>
    private static void InitializeHumanClientState(GameConnection connection, Character character, IEnumerable<House> houses)
    {
        connection.SendPacket(new SCCharacterStatePacket(character));
        connection.SendPacket(new SCCharacterGamePointsPacket(character));
        character.Inventory.Send();
        connection.SendPacket(new SCActionSlotsPacket(character.Slots));

        character.Quests.Send();
        character.Quests.SendCompleted();

        character.Actability.Send();
        character.Mails.SendUnreadMailCount();
        character.Appellations.Send();
        character.Portals.Send();
        character.Friends.Send();
        character.Blocked.Send();

        foreach (var house in houses)
        {
            connection.SendPacket(new SCMyHousePacket(house));
        }

        foreach (var conflict in ZoneManager.Instance.GetConflicts())
        {
            connection.SendPacket(new SCConflictZoneStatePacket(conflict.ZoneGroupId, conflict.CurrentZoneState, conflict.NextStateTime));
        }

        FactionManager.Instance.SendFactions(character);
        FactionManager.Instance.SendRelations(character);
        ExpeditionManager.Instance.SendExpeditions(character);

        if (character.Expedition != null)
        {
            ExpeditionManager.SendExpeditionInfo(character);
        }

        character.SendOption(1);
        character.SendOption(2);
        character.SendOption(5);
    }

    /// <summary>
    /// Shared post-entry state (human + headless): login buff, race/gender
    /// template buffs, persistent buffs from DB, wanted threshold, gear
    /// bonuses, saved HP/MP restore, breath, and zone-entry handling.
    /// Verbatim order from the pre-extraction CSSelectCharacterPacket.Read().
    /// </summary>
    private static void FinalizeActivationState(Character character)
    {
        character.Buffs.AddBuff((uint)BuffConstants.LoggedOn, character);

        var template = CharacterManager.Instance.GetTemplate(character.Race, character.Gender);

        foreach (var buff in template.Buffs)
        {
            var buffTemplate = SkillManager.Instance.GetBuffTemplate(buff);
            var casterObj = new SkillCasterUnit(character.ObjId);
            character.Buffs.AddBuff(new Buff(character, character, casterObj, buffTemplate, null, DateTime.UtcNow) { Passive = true });
        }

        // Load persistent buffs from database
        character.Buffs.LoadActiveBuffs(character);
        character.CheckWantedThreshold();

        character.UpdateGearBonuses(null, null);
        character.RestoreSavedHpMp();

        character.Breath = character.LungCapacity;

        character.OnZoneChange(0, character.Transform.ZoneId);
    }
}
