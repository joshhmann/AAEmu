using AAEmu.Commons.Utils;
using AAEmu.Game.Core.Managers;
using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Models.Game.Skills;
using AAEmu.Game.Models.Game.Skills.Static;
using AAEmu.Game.Models.Game.Units;
using AAEmu.Game.Models.Game.Units.Static;
using AAEmu.Game.Models.Game.World.Zones;
using AAEmu.Game.Models.StaticValues;

using NLog;

namespace AAEmu.Game.Models.Game.Char;

/// <summary>
/// Character resurrection core — extracted verbatim from
/// CSResurrectCharacterPacket so the SAME real engine path serves both the
/// client packet handler (a human clicking revive) and the M6.2 bot death
/// watch (headless bots have no client to send the packet).
///
/// Semantics preserved from the packet path:
///  - portal selection: Temple Priestess (npc 502) inside instances,
///    war-zone faction return points, else the closest return portal
///    (PortalManager.GetClosestReturnPortal — fail-safe = current position)
///  - inPlace (player-res): ResurrectHp/MpPercent restore, no debuffs;
///    otherwise 10% HP/MP
///  - SCCharacterResurrectedPacket + SCUnitPointsPacket broadcasts
///  - revival debuffs: PvP-war-zone → Leech + Respawn-CD; PvP → Respawn-CD;
///    PvE → Weakened Body + Respawn-CD (5 min)
///  - IsUnderWater reset + full breath
///
/// Server-side relocation is NOT part of the shared core: the retail packet
/// path lets the client re-enter at the portal. Headless bots pass a
/// <paramref name="serverRelocator"/> (the scheduler supplies the real
/// region-aware Character.SetPosition move).
/// </summary>
public static class CharacterResurrection
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Duration of the post-revive Respawn-Cooldown debuff in milliseconds (5 min).</summary>
    private const int RespawnCooldownDurationMs = 300_000;

    /// <summary>
    /// Resurrects the character through the real engine path. Returns the
    /// chosen portal (fail-safe: current position when none found).
    /// <paramref name="closestPortalResolver"/> overrides the portal lookup
    /// for rigs; <paramref name="serverRelocator"/> performs an optional
    /// server-side move to the portal (bot path only — never the packet).
    /// </summary>
    public static Portal Resurrect(Character character, bool inPlace,
        Func<Character, Portal>? closestPortalResolver = null,
        Action<Character, Portal>? serverRelocator = null)
    {
        // Portal selection. When a resolver is injected (rigs), it IS the
        // selection — the instance/war-zone/closest-portal chain below is
        // the real production path and only runs without an override (it
        // touches DI-only managers — ZoneManager/PortalManager — that
        // headless rigs cannot instantiate).
        Portal portal;
        if (closestPortalResolver != null)
        {
            portal = closestPortalResolver(character) ?? new Portal();
        }
        else
        {
        portal = new Portal();

        // поищем сначала "UnitId": 502, "Title": "Temple Priestess",
        // Inside dungeons or other instances, just respawn at the nearest Priestess
        // (ParentWorld null-guard: headless rigs have no world attached)
        if (character.Transform.InstanceId != WorldManager.DefaultInstanceId && character.ParentWorld != null)
        {
            var npcs = character.ParentWorld.GetAllNpcs();
            foreach (var npc in npcs.Where(npc => npc.TemplateId == 502))
            {
                portal.WorldId = character.Transform.WorldId;
                portal.ZoneId = npc.Transform.ZoneId;
                portal.X = npc.Transform.World.Position.X + Random.Shared.Next(1, 3);
                portal.Y = npc.Transform.World.Position.Y + Random.Shared.Next(1, 3);
                portal.Z = npc.Transform.World.Position.Z;
                portal.ZRot = npc.Transform.World.Rotation.Z;
                portal.Yaw = npc.Transform.World.Rotation.Z;
                break;
            }
        }
        else
        {
            // Check if the current zone is at War and if it has special respawn areas for factions
            var usePortalId = 0u;
            var currentZone = ZoneManager.Instance.GetZoneByKey(character.Transform.ZoneId);
            if (currentZone != null)
            {
                var conflictData = ZoneManager.Instance.GetConflicts().FirstOrDefault(c => c.ZoneGroupId == currentZone.GroupId);
                if (conflictData?.CurrentZoneState == ZoneConflictType.War)
                {
                    switch (character.Faction.MotherId)
                    {
                        case FactionsEnum.NuiaAlliance:
                            usePortalId = conflictData.NuiaReturnPointId;
                            break;
                        case FactionsEnum.HaranyaAlliance:
                            usePortalId = conflictData.HariharaReturnPointId;
                            break;
                    }
                }
            }

            // Try to get a faction specific respawn
            if (usePortalId > 0)
            {
                portal = PortalManager.Instance.GetRespawnById(usePortalId);
            }

            // Find the closest return portal (in the world) for the player if none has been found yet
            if (usePortalId == 0 || portal == null)
            {
                portal = PortalManager.Instance.GetClosestReturnPortal(character);
            }
        }
        }

        if (inPlace)
        {
            character.Hp = (int)(character.MaxHp * (character.ResurrectHpPercent / 100.0f));
            character.Mp = (int)(character.MaxMp * (character.ResurrectMpPercent / 100.0f));
            character.ResurrectHpPercent = 1;
            character.ResurrectMpPercent = 1;
            character.PostUpdateCurrentHp(character, 0, character.Hp, KillReason.Unknown);
        }
        else
        {
            character.Hp = (int)(character.MaxHp * 0.1);
            character.Mp = (int)(character.MaxMp * 0.1);
            character.PostUpdateCurrentHp(character, 0, character.Hp, KillReason.Unknown);
        }

        if (portal.X != 0)
        {
            character.BroadcastPacket(
                new SCCharacterResurrectedPacket(
                    character.ObjId,
                    portal.X,
                    portal.Y,
                    portal.Z,
                    portal.ZRot
                ),
                true
            );
        }
        else
        {
            character.BroadcastPacket(
                new SCCharacterResurrectedPacket(
                    character.ObjId,
                    character.Transform.World.Position.X,
                    character.Transform.World.Position.Y,
                    character.Transform.World.Position.Z,
                    0
                ),
                true
            );
        }

        character.BroadcastPacket(
            new SCUnitPointsPacket(
                character.ObjId,
                character.Hp,
                character.Mp
            ),
            true
        );

        // Route death-debuffs based on death context (set by Character.DoDie).
        ApplyRevivalDebuffs(character, inPlace);

        character.IsUnderWater = false;
        character.Breath = character.LungCapacity;

        // Headless bots have no client to re-enter at the portal — the
        // caller (scheduler death watch) relocates server-side through the
        // real region-aware move.
        serverRelocator?.Invoke(character, portal);

        return portal;
    }

    /// <summary>
    /// Apply post-revive debuffs based on the death context:
    ///   inPlace (player-res) → no debuffs at all
    ///   DiedInPvpWarZone     → Leech + 5 min Respawn-CD
    ///   DiedInPvp            → 5 min Respawn-CD only (no Weakened Body)
    ///   PvE death            → Weakened Body + 5 min Respawn-CD
    /// </summary>
    private static void ApplyRevivalDebuffs(Character character, bool inPlace)
    {
        if (inPlace)
        {
            // Player-resurrected (e.g. by another player's resurrect skill): no debuffs.
            character.DiedInPvpWarZone = false;
            character.DiedInPvp = false;
            return;
        }

        var casterObj = new SkillCasterUnit(character.ObjId);

        if (character.DiedInPvpWarZone)
        {
            // PvP death in War zone → Leech + Respawn-CD
            character.DiedInPvpWarZone = false;
            character.DiedInPvp = false;
            ApplyBuff(character, casterObj, (uint)BuffConstants.WarZoneLeech);
            ApplyBuff(character, casterObj, (uint)BuffConstants.RespawnCooldown, RespawnCooldownDurationMs);
        }
        else if (character.DiedInPvp)
        {
            // PvP death outside War zone → Respawn-CD only (no Weakened Body)
            character.DiedInPvp = false;
            ApplyBuff(character, casterObj, (uint)BuffConstants.RespawnCooldown, RespawnCooldownDurationMs);
        }
        else
        {
            // PvE death → Weakened Body + Respawn-CD
            ApplyBuff(character, casterObj, (uint)BuffConstants.WeakenedBody);
            ApplyBuff(character, casterObj, (uint)BuffConstants.RespawnCooldown, RespawnCooldownDurationMs);
        }
    }

    private static void ApplyBuff(Character character, SkillCasterUnit casterObj, uint buffId, int forcedDurationMs = 0)
    {
        // PeekInstance: revival debuffs are best-effort — headless rigs (and
        // any context without a constructed SkillManager) skip them rather
        // than throwing from Singleton<T>.Instance's creation path.
        var template = Singleton<SkillManager>.PeekInstance?.GetBuffTemplate(buffId);
        if (template == null)
            return;

        var buff = new Buff(character, character, casterObj, template, null, DateTime.UtcNow);
        if (forcedDurationMs > 0)
            character.Buffs.AddBuff(buff, forcedDuration: forcedDurationMs);
        else
            character.Buffs.AddBuff(buff);
    }
}
