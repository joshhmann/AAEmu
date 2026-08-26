using System.Collections.Frozen;


using NLog;

namespace AAEmu.Game.Models.Game.NPChar;

/// <summary>
/// PB-005 remedies C+A (branch fix/pb005-grounding, 2026-08-26).
///
/// Remedy C — intentional-floater whitelist. The only pre-existing exclusion is
/// <see cref="Core.Managers.ModelManager.IsFlyOrSwim"/> (actor_models.movement_id = 2,
/// 58 models). The offline grounding audit
/// (scorecard-explorations/generated/npc-grounding-audit-2026-08-25.md, §4–§5) showed a
/// large share of the severe-float population are units that legitimately do NOT stand on
/// terrain: aerial/water species whose model lacks movement_id=2, and town/structure
/// dwellers whose stand surface is a deck/floor mesh above the raw heightmap. Those are
/// whitelisted here as DATA (template-id set + one-line justification per entry) so that
/// the runtime clamp (remedy A) never "corrects" them down to terrain.
///
/// Deliberately NOT whitelisted (genuinely bad source data — the clamp must fix them):
/// - e_hasla frozen-z batch: Citizens 12335/12336/12337/12340/12341, Maid 12339, Ravra 9631,
///   plus every Guard/Sentry spawned inside it (10791/10793 rows at z=538.6/542.3/488.7 over
///   355–444 terrain — same flat-value extraction corruption; outside that batch these
///   families measure terrain-grounded, median |dz| ≈ 0.1 m);
/// - everything without structural or species evidence (Monstrous Mimic 10082, Shadowhawk
///   bandits, Honor Point Collector 7054, ...): the clamp's throttled warning is the
///   telemetry that surfaces any misclassification.
///
/// Remedy A — conservative positive-offset threshold for the spawn-time Z clamp applied by
/// <see cref="NpcSpawnerNpc.SpawnNpc"/>: ground units whose spawner Z is more than
/// <see cref="ClampSeverityM"/> above the sampled terrain are snapped to that terrain height.
/// Smaller offsets preserve road/deck placement, and negative offsets preserve possible
/// cave/interior floors because terrain-only data cannot identify those meshes.
/// </summary>
public static class NpcGroundingPolicy
{
    public const float ClampSeverityM = 2f;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // ---------------------------------------------------------------
    // Intentional-floater whitelist (remedy C)
    // ---------------------------------------------------------------

    /// <summary>
    /// NPC template ids exempt from spawn-time Z clamping, each with its evidence from the
    /// 2026-08-25 grounding audit. Grouped: (1) aerial/water species missed by
    /// ModelManager.IsFlyOrSwim; (2) Crimson Rift anomalies that hover by design;
    /// (3) structure dwellers standing on decks/piers/floors above raw terrain heightmap.
    /// </summary>
    private static readonly FrozenSet<uint> IntentionalFloaters = new[]
    {
        // --- (1a) birds / aerial species (movement_id != 2) ---
        1243u, // Purple Falcon — aerial raptor; hovers over valleys (61 spawns, dz 9.6–119.6)
        2220u, // Giant Crow — aerial
        2306u, // Whitetail Eagle — aerial
        2307u, // Plains Razorbeak — bird of prey
        2492u, // Infectious Razorbeak — bird of prey
        2500u, // Stone Coast Razorbeak — bird of prey
        3345u, // Flamehawk — aerial
        3709u, // Azure Owl — aerial
        4174u, // Royal Falcon — aerial
        4175u, // Bloodbeak Hawk — aerial
        4208u, // Whitetail Eagle — aerial
        8019u, // Pale Razorbeak — bird of prey
        9171u, // Sanddeep Razorbeak — bird of prey
        1363u, // Harpy Wind Mage — winged caster
        14667u,// Browntail Harpy — winged
        2022u, // Bat — flying
        3451u, // Carnivorous Bee — flying insect

        // --- (1b) water species at/below ocean surface (movement_id != 2) ---
        3155u, // Archerfish — aquatic
        3704u, // Piranha — aquatic
        8022u, // White Shark — aquatic
        8564u, // Mother Seabug — aquatic
        8566u, // Seabug Pupa — aquatic
        8608u, // Deep Ocean Striped Shark — aquatic
        8609u, // Striped Shark Pup — aquatic
        8616u, // Ocean Razorbeak — drifts at ocean surface (dz ≈ +100 = OceanLevel above sea floor)
        9426u, // Starsand Jellyfish — aquatic
        9427u, // Starsand Striped Shark — aquatic
        10801u,// Golden Jellyfish — aquatic
        10820u,// Jellyfish — aquatic
        12638u,// Young Jellyfish — aquatic
        8009u, // Sluggish Seabug — aquatic
        5004u, // Lost Seabug — aquatic
        8033u, // Ynystere Seafolk Warrior — merfolk at water surface
        8034u, // Ynystere Seafolk Wizard — merfolk at water surface
        9459u, // Seafolk Herman — merfolk at water surface

        // --- (2) Crimson Rift anomalies — rift spawns hover above terrain by design ---
        8051u, // Ynystere Crimson Rift
        8052u, // Ynystere Crimson Rift Rank 1 Summon
        8053u, // Ynystere Crimson Rift Rank 2 Summon
        8828u, // Crimson Rift

        // --- (3a) Two Crowns harbor/town structures (zone 264; audit §5b deck heights) ---
        5508u, // Auctioneer — market platform
        9920u, // Eokad — noble household pet kept on decks (constant +4.2 m)
        11844u,// Two Crowns Noble — structure decks (constant +4.2 m baseline)
        11845u,// Two Crowns Noble — structure decks
        11846u,// Two Crowns Noble — structure decks
        11847u,// Two Crowns Noble — structure decks
        11913u,// Two Crowns Noble's Son — structure decks
        11914u,// Two Crowns Noble's Daughter — structure decks
        11915u,// Two Crowns Townsperson — constant +4.2 m deck height
        11916u,// Two Crowns Townsperson — constant +4.2 m deck height
        11917u,// Two Crowns Townsperson — constant +4.2 m deck height
        11918u,// Two Crowns Townsperson — constant +4.2 m deck height
        11919u,// Two Crowns Noble's Son — structure decks
        11920u,// Two Crowns Noble's Daughter — structure decks
        11921u,// Two Crowns Noble — structure decks
        11922u,// Two Crowns Noble — structure decks
        11848u,// Royal Knight — palace deck (+4.2..11 m)
        11849u,// Royal Guard — palace deck (+4.2..11 m); Two-Crowns-only unit, clean evidence
        11853u,// Maid (Two Crowns household) — deck offsets 0.1..10.8 m

        // --- (3b) Salphira temple upper floor (constant +13.9 m over terrain) ---
        10141u,// Salphira Disciple — temple floor
        10142u,// Salphira Disciple — temple floor
        12058u,// Salphira Disciple — temple floor
        12059u,// Salphira Disciple — temple floor
        12060u,// Salphira Disciple — temple floor
        12003u,// Salphira Shrine Escort — temple floor
        12004u,// Salphira Shrine Escort — temple floor

        // --- (3c) Seven Bridges crossings (zone 280; bridge decks +9..18 m) ---
        5000u, // Seven Bridges Villager — bridge deck
        5001u, // Seven Bridges Villager — bridge deck
        5080u, // Busy Pirate — trading on bridge/deck level

        // --- (3d) Blacksail pirate ship decks (zone 195; crew stands on hull/superstructure) ---
        2921u, // Blacksail Crewmember
        2924u, // Blacksail Cannoneer
        2936u, // Blacksail Medic
        2937u, // Blacksail Cook Shoota
        2938u, // Blacksail Medic Whitney
        2939u, // Cannon Officer Wyld
        2940u, // First Mate Lamelda

        // --- (3e) piers / docks / floating markets / auction platforms (structures over water) ---
        658u,  // Auctioneer — market platform
        872u,  // Auctioneer — market platform
        3570u, // Auctioneer — market platform
        9664u, // Auctioneer (market keeper) — market platform
        2250u, // Fishmonger Kuata — floating-market stall
        2251u, // Fishmonger Ponsa — floating-market stall
        2408u, // Wharf Fisherman — pier deck
        3338u, // Fisherman Heissen — pier deck
        3624u, // Angler — pier deck
        9897u, // Angler Kuruhara — pier deck
        3575u, // Dock Worker — dock deck
        4947u, // Dock Worker Dewie — dock deck
    }.ToFrozenSet();

    /// <summary>True when this npc template is exempt from spawn-time Z clamping (remedy C).</summary>
    public static bool IsIntentionalFloater(uint npcTemplateId) => IntentionalFloaters.Contains(npcTemplateId);

    // ---------------------------------------------------------------
    // Spawn-time clamp decision (remedy A)
    // ---------------------------------------------------------------

    public enum SpawnGroundingAction
    {
        /// <summary>Fly/swim or whitelisted unit — spawner z used verbatim (legacy behavior).</summary>
        Exempted,
        /// <summary>No usable ground sample (GeoData exception / out-of-bounds sentinel 0).</summary>
        NoGroundSample,
        /// <summary>Sub-2 m offsets and negative offsets (cave/interior suspects) preserve source z.</summary>
        KeptSourceZ,
        /// <summary>Positive offset at or above severity threshold — z snapped to terrain.</summary>
        ClampedToGround,

    }

    /// <summary>
    /// Pure decision core of the SpawnNpc clamp. Returns the resolved z and the action taken;
    /// callers emit telemetry via <see cref="ReportClamp"/> when the action is ClampedToGround.
    /// </summary>
    public static SpawnGroundingAction ResolveSpawnZ(uint npcTemplateId, bool canFly, float spawnerZ, float groundZ, out float resolvedZ)
    {
        resolvedZ = spawnerZ;

        if (canFly || IsIntentionalFloater(npcTemplateId))
            return SpawnGroundingAction.Exempted;

        // GeoData.GetHeight returns 0 both on exception and out-of-bounds cells; snapping to
        // 0 (or a non-finite sample) would drop the NPC into the void, so an unusable sample
        // keeps the source z.
        if (!float.IsFinite(spawnerZ) || !float.IsFinite(groundZ) || groundZ <= 0f)
            return SpawnGroundingAction.NoGroundSample;

        var offset = spawnerZ - groundZ;
        if (offset < ClampSeverityM)
            return SpawnGroundingAction.KeptSourceZ;

        resolvedZ = groundZ;
        return SpawnGroundingAction.ClampedToGround;
    }

    // ---------------------------------------------------------------
    // Throttled warning telemetry
    // ---------------------------------------------------------------

    private static readonly object WarnGate = new();
    private static TimeSpan _warnInterval = TimeSpan.FromSeconds(10);
    private static DateTime _nextWarnUtc = DateTime.MinValue;
    private static long _suppressedSinceLastWarn;

    /// <summary>Test hook: resets throttle state and interval.</summary>
    internal static void ResetWarnThrottleForTests(TimeSpan? interval = null)
    {
        lock (WarnGate)
        {
            _warnInterval = interval ?? TimeSpan.FromSeconds(10);
            _nextWarnUtc = DateTime.MinValue;
            _suppressedSinceLastWarn = 0;
        }
    }

    /// <summary>Test hook: expires the current window without discarding suppressed count.</summary>
    internal static void ExpireWarnThrottleForTests()
    {
        lock (WarnGate)
            _nextWarnUtc = DateTime.MinValue;
    }

    /// <summary>
    /// Emits at most one warning per <see cref="_warnInterval"/>, aggregating suppressed
    /// occurrences into the next emitted line. Names template id + coordinates as required
    /// by the PB-005 fix contract.
    /// </summary>
    public static void ReportClamp(uint npcTemplateId, float x, float y, float spawnerZ, float groundZ)
    {
        lock (WarnGate)
        {
            var now = DateTime.UtcNow;
            if (now < _nextWarnUtc)
            {
                _suppressedSinceLastWarn++;
                return;
            }

            _nextWarnUtc = now + _warnInterval;
        }

        var suppressed = Interlocked.Exchange(ref _suppressedSinceLastWarn, 0);
        if (suppressed > 0)
            Logger.Warn(
                "PB-005 spawn grounding: clamped npc template {TemplateId} at ({X:F1},{Y:F1}) z {SpawnerZ:F1} -> {GroundZ:F1} ({Suppressed} further clamps suppressed since last warning)",
                npcTemplateId, x, y, spawnerZ, groundZ, suppressed);
        else
            Logger.Warn(
                "PB-005 spawn grounding: clamped npc template {TemplateId} at ({X:F1},{Y:F1}) z {SpawnerZ:F1} -> {GroundZ:F1}",
                npcTemplateId, x, y, spawnerZ, groundZ);
    }
}
