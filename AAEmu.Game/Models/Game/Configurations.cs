using AAEmu.Commons.Network;
// ReSharper disable ClassNeverInstantiated.Global

namespace AAEmu.Game.Models.Game;

public enum WindModelType
{
    /// <summary>Retail-like: wind only along N↔S axis. 15 angle bonus for wind in the direction of the ship.</summary>
    Official,
    /// <summary>More realistic: wind direction rotates smoothly over the day.</summary>
    Realistic
}

public enum SeaWeatherModelType
{
    /// <summary>Retail-like sea weather ship behavior (default).</summary>
    Official,
    /// <summary>Experimental/realistic sea weather ship behavior.</summary>
    Realistic
}

public class Configurations : PacketMarshaler
{
    public string Key { get; set; }
    public string Value { get; set; }
}

public class WorldConfig
{
    /// <summary>
    /// Message of the Day that gets displayed in player's chat upon login
    /// </summary>
    public string MOTD { get; set; } = "";

    /// <summary>
    /// Message shown to the player when they exit the game
    /// </summary>
    public string LogoutMessage { get; set; } = "";

    /// <summary>
    /// Time in minutes between user data Save events
    /// </summary>
    public double AutoSaveInterval { get; set; } = 5.0;

    /// <summary>
    /// Server-side Exp multiplier (on top of buffs)
    /// </summary>
    public double ExpRate { get; set; } = 1.0;

    /// <summary>
    /// Server-side Honor Points multiplier (on top of buffs)
    /// </summary>
    public double HonorRate { get; set; } = 1.0;

    /// <summary>
    /// Separate multiplier for PvP Honor Points (kills in Conflict/War zones). Independent of HonorRate.
    /// </summary>
    public double PvpHonorRate { get; set; } = 1.0;

    /// <summary>
    /// Server-side Vocation Badge multiplier (on top of buffs)
    /// </summary>
    public double VocationRate { get; set; } = 1.0;

    /// <summary>
    /// Multiplier for the loot dice (some loot types are not affected by this)
    /// </summary>
    public double LootRate { get; set; } = 1.0;

    /// <summary>
    /// Multiplier for gold that is obtained through loot drops
    /// </summary>
    public double GoldLootMultiplier { get; set; } = 1.0;

    /// <summary>
    /// Multiplier for growth rate of doodads, note that this only affects steps marked as growth and not those with a simple timer.
    /// </summary>
    public double GrowthRate { get; set; } = 1.0;

    /// <summary>
    /// Number of days 1 week worth of tax pays for, set this to 3640 would make 1 tax payment last for about 10 years.
    /// </summary>
    public uint DaysForTaxPayment { get; set; } = 7u;

    /// <summary>
    /// Set a minimum access-level that a character must have to ignore falling damage (for devs)
    /// </summary>
    public int IgnoreFallDamageAccessLevel { get; set; } = 100;

    /// <summary>
    /// When enabled, players take no damage at all
    /// </summary>
    public bool GodMode { get; set; }

    /// <summary>
    /// Legacy test behavior: boot open conflict zones straight into the Conflict state.
    /// Default (false) follows the 1.2 conflict cycle: zones boot into Peace, the
    /// shielded phase, and escalate via kill counters and the state timer chain.
    /// Configure in <c>AAEmu.Game/Configurations/World.json</c> under <c>World.ConflictZonesStartAtConflict</c>.
    /// </summary>
    public bool ConflictZonesStartAtConflict { get; set; }

    /// <summary>
    /// Enables the loading of NavMesh data for dungeons
    /// </summary>
    public bool GeoDataMode { get; set; }

    /// <summary>
    /// When false, heightmaps get loaded on-demand only. Should increase boot times and lower memory use
    /// </summary>
    // TODO: Also apply this to missionX.bai files
    public bool PreLoadTerrain { get; set; }

    /// <summary>
    /// Maximum number of instances that can be created (includes system instances)
    /// </summary>
    public uint MaxInstances { get; set; } = 32;

    /// <summary>
    /// Target Ticks per Second to use for Physics threads
    /// </summary>
    public float TargetPhysicsTps { get; set; } = 25f;

    /// <summary>
    /// Ship wind model used for open-sea wind when there is no river flow (<see cref="Slave.CachedWaterFlow"/> is zero).
    /// Configure in <c>AAEmu.Game/Configurations/World.json</c> under <c>World.WindModel</c>.
    ///
    /// Allowed values:
    /// - <c>Official</c>: retail-like. Wind does NOT change with time of day and gives a hard +15% bonus
    ///   only when sailing within ±15° of the N↔S axis (both directions). Outside the cone the bonus is 0%.
    /// - <c>Realistic</c>: wind direction rotates smoothly over the day (and the existing rig profile logic applies).
    /// Default: <c>Official</c>.
    /// </summary>
    public WindModelType WindModel { get; set; } = WindModelType.Official;

    /// <summary>
    /// Sea weather ship model used for marine weather effects (whirlpool / storm cloud).
    /// Configure in <c>AAEmu.Game/Configurations/World.json</c> under <c>World.SeaWeatherModel</c>.
    /// Default: <c>Official</c>.
    /// </summary>
    public SeaWeatherModelType SeaWeatherModel { get; set; } = SeaWeatherModelType.Official;

    /// <summary>
    /// Server-side Actability Points multiplier (on top of buffs)
    /// </summary>
    public double ActabilityRate { get; set; } = 1.0;

    /// <summary>
    /// When true, monster kill credits and quest progress are shared across every
    /// player that dealt damage to the mob plus their party / raid mates that are
    /// within 200m at time of death. When false (default = vanilla behaviour), only
    /// the killer (and the killer's team for tagged kills) earns the credit.
    /// </summary>
    public bool TagShareEnabled { get; set; } = false;

    /// <summary>
    /// When true (default), NPC aggro acquisition (<c>Behavior.CheckAggression</c> /
    /// <c>CheckAlert</c>) requires a line of sight to the target: terrain heights are
    /// sampled along the sight line and the mob will not aggro through hills, ridges,
    /// or cliffs. Set to false to restore the legacy distance+FOV-only acquisition
    /// (mobs aggro through terrain again). Configure in
    /// <c>AAEmu.Game/Configurations/World.json</c> under <c>World.NpcLineOfSightCheck</c>.
    /// </summary>
    public bool NpcLineOfSightCheck { get; set; } = true;

    /// <summary>
    /// When true, housing bound doodads (doors, windows, planters, drills, animals) are saved to the
    /// database and their state (open/closed, fill level, growth phase) is restored on server restart.
    /// When false (default), bound doodads are re-created fresh from template data on every restart,
    /// matching the original behaviour.
    /// Configure in <c>AAEmu.Game/Configurations/World.json</c> under <c>World.UsePersistentHouseDoodads</c>.
    /// </summary>
    public bool UsePersistentHouseDoodads { get; set; } = false;

    /// <summary>
    /// Rate of exp lost on death by NPCs
    /// Default is 5% (0.05f)
    /// </summary>
    public float ExpLossRateAtDeath { get; set; } = 0.05f;

    /// <summary>
    /// Multiplier to apply to normal wear and tear rate of equipment vs NPCs
    /// </summary>
    public float PvEDurabilityLossRate { get; set; } = 1.0f;

    /// <summary>
    /// Multiplier to apply to normal wear and tear rate of equipment vs other players
    /// </summary>
    public float PvPDurabilityLossRate { get; set; } = 1.0f;

    /// <summary>
    /// Level at which a player starts losing exp and durability on death
    /// </summary>
    public int MinimumExpLossLevel { get; set; } = 10;

    /// <summary>
    /// Max vertical rise (in meters) an NPC may climb in a single movement tick
    /// before the slope/step gate in <c>Npc.MoveTowards</c> rejects the step.
    /// Stops NPCs from walking straight up cliff faces and steep slopes where
    /// navmesh data is missing (chase/roam beeline would otherwise climb any
    /// wall at full speed). Flat ground is unaffected (height delta ~ 0);
    /// downward steps are never blocked. Set to 0 to disable the gate entirely.
    /// Default: 0.5 — typical walkable step height (≈ client step-up).
    /// Configure in <c>AAEmu.Game/Configurations/World.json</c> under <c>World.NpcMaxStepHeight</c>.
    /// </summary>
    public float NpcMaxStepHeight { get; set; } = 0.5f;

    /// <summary>
    /// Physics-loop telemetry for the A5 stall investigation. Disabled by
    /// default (default-safe): when enabled, each physics iteration is sampled
    /// into bounded rings (loop gap, sleep overshoot, Step duration, broadcast
    /// duration, pending-action/body/ship/force counts) and a periodic aggregate
    /// log line is emitted (WARN on a slow window, DEBUG otherwise). Configure
    /// in <c>AAEmu.Game/Configurations/World.json</c> under <c>World.PhysicsTelemetry</c>.
    /// </summary>
    public PhysicsTelemetryConfig PhysicsTelemetry { get; set; } = new();
}

/// <summary>
/// Configuration for per-iteration physics telemetry (A5 stall investigation).
/// All values are safe defaults; telemetry is OFF unless <see cref="Enabled"/>.
/// </summary>
public class PhysicsTelemetryConfig
{
    /// <summary>Master switch. When false (default) no samples are recorded and no log lines are emitted.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Seconds between periodic aggregate log lines (default 60).</summary>
    public double SamplePeriodSeconds { get; set; } = 60;

    /// <summary>
    /// Loop-gap threshold (ms) above which the periodic aggregate is logged at
    /// WARN instead of DEBUG (default 100).
    /// </summary>
    public double SlowIterationMs { get; set; } = 100;

    /// <summary>
    /// Minimum sample period (seconds). Values below this are clamped up.
    /// </summary>
    public const double MinSamplePeriodSeconds = 1;

    /// <summary>
    /// Maximum sample period (seconds). Values above this are clamped down so
    /// ring-capacity arithmetic (period × physics TPS) stays bounded and can
    /// never overflow or allocate unboundedly (1h × 25 TPS ≈ 90k samples/ring).
    /// </summary>
    public const double MaxSamplePeriodSeconds = 3600;

    /// <summary>
    /// Maximum slow-iteration threshold (ms). Values above this — including
    /// +Infinity — are clamped down so the normalized threshold is always
    /// finite. A threshold above 60s is meaningless for a ~40ms physics step
    /// (it would effectively disable the WARN path), so the bound is safe.
    /// </summary>
    public const double MaxSlowIterationMs = 60000;

    /// <summary>
    /// Normalizes this config to safe bounds (clamp, matching the
    /// <c>PlayerBotScheduler</c> WorkerCount convention): sample period is
    /// clamped to [<see cref="MinSamplePeriodSeconds"/>, <see cref="MaxSamplePeriodSeconds"/>]
    /// and the slow-iteration threshold is clamped to
    /// [0, <see cref="MaxSlowIterationMs"/>]. NaN and infinities are replaced
    /// with the safe defaults / bounds (Math.Clamp/Math.Max alone pass NaN
    /// through, which would poison ring-capacity arithmetic; +Infinity would
    /// otherwise survive the &lt; 0 check). <see cref="Enabled"/> is preserved
    /// (default false). Returns a NEW normalized copy — the caller's shared
    /// config instance is never mutated.
    /// </summary>
    public PhysicsTelemetryConfig Normalize()
    {
        var period = SamplePeriodSeconds;
        if (double.IsNaN(period))
            period = 60; // default
        else if (period < MinSamplePeriodSeconds)
            period = MinSamplePeriodSeconds;
        else if (period > MaxSamplePeriodSeconds)
            period = MaxSamplePeriodSeconds;

        var slow = SlowIterationMs;
        if (double.IsNaN(slow))
            slow = 100; // default
        else if (slow < 0)
            slow = 0;
        else if (slow > MaxSlowIterationMs)
            slow = MaxSlowIterationMs;

        return new PhysicsTelemetryConfig
        {
            Enabled = Enabled,
            SamplePeriodSeconds = period,
            SlowIterationMs = slow
        };
    }
}

public class DungeonLoadConfig
{
    public string Name { get; set; } = string.Empty;
    public uint Channel { get; set; } = 0;
    public uint Id { get; set; } = 0;
}

public class DungeonsConfig
{
    /// <summary>
    /// If people are kicked from a dungeon and there are no people left,
    /// should the system automatically remove the dungeon instance (default=yes, retail=no) 
    /// </summary>
    public bool AutoCleanupAfterKick { get; set; } = true;

    /// <summary>
    /// Time in seconds after being removed from a party in a dungeon before you get kicked out
    /// </summary>
    public int AutoTeamDisbandKickTime { get; set; } = 30;

    /// <summary>
    /// List of dungeon instances that should be created by default
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    public List<DungeonLoadConfig> AutoCreate { get; set; } = [];
}

public class AccountDeleteDelayTiming
{
    /// <summary>
    /// Minimum Level this timing applies to
    /// </summary>
    public int Level { get; set; }
    /// <summary>
    /// Delay in minutes that needs to be used if this character is at least this level
    /// </summary>
    public int Delay { get; set; }
}

public class AccountConfig
{
    /// <summary>
    /// Allowed Regex for account names
    /// </summary>
    public string NameRegex { get; set; } = "^[a-zA-Z0-9]{1,18}$";
    /// <summary>
    /// Marks if a deleted character's name can be re-used for a new character
    /// </summary>
    public bool DeleteReleaseName { get; set; } = false;
    // ReSharper disable once CollectionNeverUpdated.Global
    // Populated by JSON reader
    /// <summary>
    /// Delete character settings
    /// </summary>
    public List<AccountDeleteDelayTiming> DeleteTimings { get; set; } = [];
    /// <summary>
    /// Default access-level for new accounts
    /// </summary>
    public int AccessLevelDefault { get; set; } = 0;
    /// <summary>
    /// Access-Level that should be used for the first created account on the server regardless of other settings
    /// </summary>
    public int AccessLevelFirstAccount { get; set; } = 100;
    /// <summary>
    /// Access-Level that should be used for the first created character on the server regardless of other settings
    /// </summary>
    public int AccessLevelFirstCharacter { get; set; } = 100;
}

public class CurrencyValuesConfig
{
    public int Default { get; set; } = 0;
    public int DailyLogin { get; set; } = 0;
    public int TickMinutes { get; set; } = 5;
    public int TickAmount { get; set; } = 0;
    public int TickAmountPremium { get; set; } = 0;

    public int GetTickAmount(bool isPremium)
    {
        return isPremium ? TickAmountPremium : TickAmount;
    }
}

/// <summary>
/// Labor regeneration posture.
/// Defaults flatten retail monetization tiers: every account gets
/// premium-grade regen without paying for it (ArcheAge-without-paywalls /
/// Unchained posture). Vanilla tiers remain selectable via Mode.
/// </summary>
public enum LaborRegenMode
{
    /// <summary>Uniform premium-grade regen and cap for every account (default).</summary>
    Unchained = 0,

    /// <summary>Reproduces the confirmed retail free/patron tier table.</summary>
    VanillaRetail = 1,
}

/// <summary>
/// Labor regeneration configuration (online AND offline — offline regen shares
/// this section's cadence by design).
///
/// Retail-source citation (scorecard-explorations/generated/
/// formula-corroboration-2026-08-25.md L1–L4, community-confirmed against
/// https://archeage.fandom.com/wiki/Labor_Points): FREE users regenerated
/// 5 labor per 5 min online only (cap 2000); PATRONS 10 per 5 min online AND
/// 10 per 5 min offline (cap 5000). Those exact values live behind
/// Mode = VanillaRetail; the shipped defaults are Unchained instead.
/// </summary>
public class LaborConfig
{
    /// <summary>Starting labor balance for brand-new accounts.</summary>
    public int Default { get; set; } = 50;

    /// <summary>Minutes between labor ticks (retail: 5).</summary>
    public int TickMinutes { get; set; } = 5;

    /// <summary>Tier posture — see <see cref="LaborRegenMode"/>.</summary>
    public LaborRegenMode Mode { get; set; } = LaborRegenMode.Unchained;

    /// <summary>VanillaRetail free-tier online tick amount (retail: 5 per tick).</summary>
    public int TickAmountVanilla { get; set; } = 5;

    /// <summary>
    /// Patron-tier tick amount (retail: 10 per tick, online and offline).
    /// In Unchained mode this is the universal rate for every account.
    /// </summary>
    public int TickAmountPatron { get; set; } = 10;

    /// <summary>Unchained universal labor cap.</summary>
    public int CapLabor { get; set; } = 5000;

    /// <summary>VanillaRetail free-account cap (retail: 2000).</summary>
    public int CapFree { get; set; } = 2000;

    /// <summary>VanillaRetail patron cap (retail: 5000).</summary>
    public int CapPremium { get; set; } = 5000;

    /// <summary>Skip cap clamping entirely — regen grants unbounded.</summary>
    public bool UnlimitedCap { get; set; } = false;

    /// <summary>
    /// Makes labor-consuming actions no-ops. HONEST SIDE EFFECT: labor-XP
    /// accrual rides the same spend path (Character.ChangeLabor evaluates the
    /// ExpByLaborPower formula on consumption), so no-drain also means no
    /// labor-XP and no actability gain charged on that spend event — this is
    /// deliberately NOT a free-XP mode.
    /// </summary>
    public bool DisableConsumption { get; set; } = false;

    /// <summary>Online regen amount per tick for the given account tier.</summary>
    public int GetOnlineTickAmount(bool isPremium) =>
        isPremium || Mode == LaborRegenMode.Unchained ? TickAmountPatron : TickAmountVanilla;

    /// <summary>
    /// Offline regen amount per tick: everyone at the patron rate in Unchained
    /// mode; patrons-only in VanillaRetail (free accounts earn nothing offline).
    /// </summary>
    public int GetOfflineTickAmount(bool isPremium) =>
        isPremium || Mode == LaborRegenMode.Unchained ? TickAmountPatron : 0;

    /// <summary>Effective labor cap for the given account tier.</summary>
    public int GetCap(bool isPremium) =>
        Mode == LaborRegenMode.Unchained ? CapLabor : isPremium ? CapPremium : CapFree;
}

public class SpecialtyConfig
{
    /// <summary>
    /// Maximum rate for speciality packs
    /// </summary>
    public int MaxSpecialtyRatio { get; set; } = 130;
    /// <summary>
    /// Minimum rate for speciality packs
    /// </summary>
    public int MinSpecialtyRatio { get; set; } = 70;
    /// <summary>
    /// Amount the trade in rate lowers for each traded pack
    /// </summary>
    public double RatioDecreasePerPack { get; set; } = 0.5f;
    /// <summary>
    /// Number of % a trade recovers every X time
    /// </summary>
    public double RatioIncreasePerTick { get; set; } = 5.0;
    /// <summary>
    /// Number of minutes between trade rate updates when selling packs
    /// </summary>
    public double RatioDecreaseTickMinutes { get; set; } = 1f;
    /// <summary>
    /// Time in minutes before a traded pack is no longer counted towards the trade rate calculation
    /// </summary>
    public double RatioRegenTickMinutes { get; set; } = 60f;

    /// <summary>
    /// Time in minutes to delay trade pack reward mail delivery. Canonical 1.2:
    /// payment is mailed 22 hours after the sale (item tooltip "판매 시 22시간 후 우편으로 대금 지급").
    /// </summary>
    public double TradePackMailDelayInMinutes { get; set; } = 1320f;

    /// <summary>
    /// Time in hours a placed trade pack stays on the ground before it despawns.
    /// Canonical 1.2: "내려놓은 등짐은 6일 후 소멸" (a placed pack disappears after 6 days;
    /// re-placing resets the timer).
    /// </summary>
    public double PlacedPackExpiryHours { get; set; } = 144f;

    /// <summary>
    /// Minutes between placed-pack expiry sweeps.
    /// </summary>
    public double PlacedPackExpiryCheckMinutes { get; set; } = 60f;

    /// <summary>
    /// Minimum character level required to craft and sell trade packs.
    /// Canonical 1.2 tooltip: "10레벨 미만은 특산품 제작/판매 불가".
    /// </summary>
    public int MinLevelToCraftSell { get; set; } = 10;
}

public class ScriptsConfig
{
    public LoadStrategyType LoadStrategy { get; set; } = LoadStrategyType.Reflection;

    public enum LoadStrategyType
    {
        Compilation,
        Reflection
    }
}
