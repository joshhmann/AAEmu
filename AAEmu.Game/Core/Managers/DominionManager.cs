using System;
using System.Collections.Generic;
using System.Linq;

using AAEmu.Commons.Utils;
using AAEmu.Commons.Utils.DB;

using AAEmu.Game.GameData.Framework;

using AAEmu.Game.Core.Managers.World;
using AAEmu.Game.Core.Network.Game;
using AAEmu.Game.Core.Packets.G2C;
using AAEmu.Game.Core.Network.Connections;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Char;
using AAEmu.Game.Models.Game.Siege;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

using MySql.Data.MySqlClient;

using NLog;

namespace AAEmu.Game.Core.Managers;

/// <summary>
/// Slice-1 dominion runtime: loads the read-only siege reference data
/// (<c>siege_zones</c>, <c>siege_settings</c>, <c>siege_plans</c>) from
/// compact.sqlite3, persists declared dominions in the MySQL <c>dominions</c>
/// table, drives the weekly Peace→Declare→Warmup→Siege→Payoff schedule cron and
/// re-broadcasts stored dominions to clients entering the world.
///
/// Explicitly out of scope (later slices): combat, siege membership, tickets,
/// HQ structures, ownership transfer battles, tax collection mail.
/// </summary>
public class DominionManager(
    IGameDataManager gameDataManager, // ensures GameDataManager.Load() runs before this Load()
    ITickManager tickManager,
    IWorldManager worldManager
) : Singleton<DominionManager>, IDominionManager
{
    private static Logger Logger { get; } = LogManager.GetCurrentClassLogger();

    /// <summary>Tax-rate scale sanity bound. Inferred from the hardcoded NationalTaxRate=500 (permille).</summary>
    public const int MaxTaxRate = 1000;
    private const int DefaultTaxRate = 50;

    private readonly object _lock = new();
    private Dictionary<uint, SiegeZoneTemplate> _siegeZones = [];
    private List<SiegeSettingTemplate> _siegeSettings = [];
    private List<SiegePlanEntry> _siegePlans = [];
    private List<DateTime> _planWeekStarts = []; // distinct week starts, ascending — the rotation period
    private Dictionary<uint, Dominion> _dominions = [];

    // Schedule cron state
    private readonly Dictionary<uint, SiegePhase> _lastAnnouncedPhase = [];
    private DateTime _lastTickUtc;
    private readonly HashSet<DateTime> _announcedPayoffs = [];

    public IReadOnlyList<SiegeZoneTemplate> SiegeZones => _siegeZones.Values.ToList().AsReadOnly();
    public IReadOnlyList<SiegeSettingTemplate> SiegeSettings => _siegeSettings.AsReadOnly();
    public IReadOnlyList<SiegePlanEntry> SiegePlans => _siegePlans.AsReadOnly();
    public IReadOnlyCollection<Dominion> Dominions
    {
        get
        {
            lock (_lock)
                return _dominions.Values.ToArray();
        }
    }

    public void Load()
    {
        using (var connection = SQLite.CreateConnection())
            LoadSiegeTemplates(connection);

        using (var mySqlConnection = MySQL.CreateConnection())
            LoadDominions(mySqlConnection);

        Logger.Info("DominionManager: Loaded {0} siege zones, {1} siege settings, {2} siege plans, {3} declared dominion(s)",
            _siegeZones.Count, _siegeSettings.Count, _siegePlans.Count, _dominions.Count);
    }

    /// <summary>Reads the three siege reference tables (GameData/loader conventions, read-only).</summary>
    internal void LoadSiegeTemplates(SqliteConnection connection)
    {
        var zones = new Dictionary<uint, SiegeZoneTemplate>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM siege_zones";
            command.Prepare();
            using (var sqliteReader = command.ExecuteReader())
            using (var reader = new SQLiteWrapperReader(sqliteReader))
            {
                while (reader.Read())
                {
                    var zone = new SiegeZoneTemplate
                    {
                        Id = reader.GetUInt32("id"),
                        ZoneGroupId = reader.GetUInt32("zone_group_id"),
                        StartSiegeWeekday = reader.GetInt32("start_siege_weekday"),
                        StartSiegeHour = reader.GetInt32("start_siege_hour"),
                        StartSiegeMin = reader.GetInt32("start_siege_min"),
                        SiegeDays = reader.GetInt32("siege_days"),
                        SiegeHours = reader.GetInt32("siege_hours"),
                        SiegeMins = reader.GetInt32("siege_mins"),
                        PayWeekday = reader.GetInt32("pay_weekday"),
                        PayHour = reader.GetInt32("pay_hour"),
                        PayMin = reader.GetInt32("pay_min"),
                        DeclareItemId = reader.GetUInt32("declare_item_id"),
                        DefenseTicketId = reader.GetUInt32("defense_ticket_id"),
                        OffenseTicketId = reader.GetUInt32("offense_ticket_id"),
                        ReinforceDefenseDelayMins = reader.GetInt32("reinforce_defense_delay_mins"),
                        DefenseMerchantId = reader.GetUInt32("defense_merchant_id"),
                        OffenseMerchantId = reader.GetUInt32("offense_merchant_id"),
                        DominionMerchantId = reader.GetUInt32("dominion_merchant_id"),
                        OpenHour = reader.GetInt32("open_hour"),
                        OpenDurationHours = reader.GetInt32("open_duration_hours"),
                        StartAuctionWeekday = reader.GetInt32("start_auction_weekday"),
                        StartAuctionHour = reader.GetInt32("start_auction_hour"),
                        StartAuctionMin = reader.GetInt32("start_auction_min"),
                        StartDeclareWeekday = reader.GetInt32("start_declare_weekday"),
                        StartDeclareHour = reader.GetInt32("start_declare_hour"),
                        StartDeclareMin = reader.GetInt32("start_declare_min"),
                        StartWarmupWeekday = reader.GetInt32("start_warmup_weekday"),
                        StartWarmupHour = reader.GetInt32("start_warmup_hour"),
                        StartWarmupMin = reader.GetInt32("start_warmup_min"),
                        OpenWeekday = reader.GetInt32("open_weekday"),
                        MonumentDoodadId = reader.GetUInt32("monument_doodad_id")
                    };

                    zones.TryAdd(zone.ZoneGroupId, zone);
                }
            }
        }

        var settings = new List<SiegeSettingTemplate>();
        using (var command = connection.CreateCommand())
        {
            // siege_settings ships without a key column (rowid-only).
            command.CommandText = "SELECT rowid AS id, total_castles, num_defenders, num_reinforcements FROM siege_settings";
            command.Prepare();
            using (var sqliteReader = command.ExecuteReader())
            using (var reader = new SQLiteWrapperReader(sqliteReader))
            {
                while (reader.Read())
                {
                    settings.Add(new SiegeSettingTemplate
                    {
                        Id = reader.GetUInt32("id"),
                        NumDefenders = reader.GetInt32("num_defenders"),
                        NumReinforcements = reader.GetInt32("num_reinforcements")
                    });
                }
            }
        }

        var plans = new List<SiegePlanEntry>();
        // siege_plans is optional rotation data; without it every zone counts as
        // weekly-active (ActiveZoneGroupsForWeek fallback).
        if (TableExists(connection, "siege_plans"))
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT * FROM siege_plans";
                command.Prepare();
                using (var sqliteReader = command.ExecuteReader())
                using (var reader = new SQLiteWrapperReader(sqliteReader))
                {
                    while (reader.Read())
                    {
                        plans.Add(new SiegePlanEntry
                        {
                            Id = reader.GetUInt32("id"),
                            ZoneGroupId = reader.GetUInt32("zone_group_id"),
                            WeekStart = reader.GetDateTime("week_start")
                        });
                    }
                }
            }
        }

        _siegeZones = zones;
        _siegeSettings = settings;
        _siegePlans = plans.OrderBy(p => p.WeekStart).ToList();
        _planWeekStarts = _siegePlans.Select(p => p.WeekStart.Date).Distinct().OrderBy(d => d).ToList();
    }

    private static bool TableExists(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = @name";
        command.Parameters.AddWithValue("@name", table);
        return command.ExecuteScalar() != null;
    }

    private void LoadDominions(MySqlConnection connection)
    {
        var dominions = new Dictionary<uint, Dominion>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT * FROM dominions";
            command.Prepare();
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    var dominion = new Dominion
                    {
                        ZoneGroupId = reader.GetUInt32("zone_group_id"),
                        ExpeditionId = reader.GetUInt32("expedition_id"),
                        ExpeditionName = reader.GetString("expedition_name"),
                        TaxRate = reader.GetInt32("tax_rate"),
                        DeclaredAt = reader.GetDateTime("declared_at")
                    };
                    dominions.TryAdd(dominion.ZoneGroupId, dominion);
                }
            }
        }

        lock (_lock)
            _dominions = dominions;
    }

    public void Initialize()
    {
        _lastTickUtc = DateTime.UtcNow;
        tickManager.OnTick.Subscribe(ScheduleTick, TimeSpan.FromSeconds(15), false, "DominionManager.ScheduleTick");
    }

    public SiegeZoneTemplate GetSiegeZoneByGroup(uint zoneGroupId) => _siegeZones.GetValueOrDefault(zoneGroupId);

    public Dominion GetDominion(uint zoneGroupId)
    {
        lock (_lock)
            return _dominions.GetValueOrDefault(zoneGroupId);
    }

    // ------------------------------------------------------------------ declare

    /// <summary>
    /// Builds the wire blob for a dominion. The territory radii, coffer seed values
    /// and timer shape mirror the previously-hardcoded DeclareDominion special
    /// effect (the only client-tested payload available); persisted fields win
    /// where a MySQL row exists. Coordinates are only meaningful at declaration
    /// time — they are not part of the persisted slice-1 state.
    /// </summary>
    public static DominionData BuildDominionData(uint zoneGroupId, uint expeditionId, uint houseId,
        float x, float y, float z, int taxRate, DateTime reignStart)
    {
        return new DominionData
        {
            House = houseId,
            X = x,
            Y = y,
            Z = z,
            TaxRate = taxRate,
            ReignStartTime = reignStart,
            ExpeditionId = expeditionId,
            CurHouseTaxMoney = 500000,
            CurHuntTaxMoney = 9000,
            PeaceTaxMoney = 300000,
            CurHouseTaxAaPoint = 0,
            PeaceTaxAaPoint = 0,
            LastPaidTime = reignStart,
            LastSiegeEndTime = reignStart,
            LastTaxRateChangedTime = reignStart,
            LastNationalTaxRateChagedTime = reignStart,
            NationalTaxRate = 500,
            NationalMonumentDbId = 0,
            NationalMonumentX = 0,
            NationalMonumentY = 0,
            NationalMonumentZ = 0,
            TerritoryData = new DominionTerritoryData
            {
                Id = 6,
                Id2 = 4771,
                MaxGates = 1,
                MaxWalls = 50,
                RadiusDeclare = 250,
                RadiusDominion = 110,
                RadiusSiege = 250,
                RadiusOffenseHq = 100
            },
            SiegeTimers = new DominionSiegeTimers
            {
                Bdm = 0,
                Durations = [0, 0, 0, 0, 0],
                Fixed = DateTime.MinValue,
                Started = DateTime.MinValue,
                SiegePeriod = 1,
                UnkData = EmptyUnkData(4),
                Unk2Data = EmptyUnkData(0)
            },
            NonPvPDuration = 0,
            NonPvPStart = reignStart,
            ZoneId = (ushort)zoneGroupId,
            ObjId = 0
        };
    }

    private static DominionUnkData EmptyUnkData(uint objId) => new()
    {
        Id = 0,
        Limit = 0,
        Ni = 0,
        Nr = 0,
        X = 0,
        Y = 0,
        Z = 0,
        ObjId = objId,
        UnkIds = []
    };

    public void Declare(DominionData dominionData, string expeditionName)
    {
        var dominion = new Dominion
        {
            ZoneGroupId = dominionData.ZoneId,
            ExpeditionId = dominionData.ExpeditionId,
            ExpeditionName = expeditionName ?? string.Empty,
            TaxRate = dominionData.TaxRate <= 0 ? DefaultTaxRate : dominionData.TaxRate,
            DeclaredAt = DateTime.UtcNow
        };

        lock (_lock)
            _dominions[dominion.ZoneGroupId] = dominion;

        PersistDominion(dominion);

        worldManager.BroadcastPacketToServer(new SCDominionDataPacket(dominionData, true, true));
        Logger.Info("Dominion declared: zone_group {0} by expedition {1} ({2})",
            dominion.ZoneGroupId, dominion.ExpeditionId, dominion.ExpeditionName);
    }

    private void PersistDominion(Dominion dominion)
    {
        using var connection = MySQL.CreateConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO dominions (zone_group_id, expedition_id, expedition_name, tax_rate, declared_at) " +
            "VALUES (@zone_group_id, @expedition_id, @expedition_name, @tax_rate, @declared_at) " +
            "ON DUPLICATE KEY UPDATE expedition_id = @expedition_id, expedition_name = @expedition_name, " +
            "tax_rate = @tax_rate, declared_at = @declared_at";
        command.Parameters.AddWithValue("@zone_group_id", dominion.ZoneGroupId);
        command.Parameters.AddWithValue("@expedition_id", dominion.ExpeditionId);
        command.Parameters.AddWithValue("@expedition_name", dominion.ExpeditionName);
        command.Parameters.AddWithValue("@tax_rate", dominion.TaxRate);
        command.Parameters.AddWithValue("@declared_at", dominion.DeclaredAt);
        command.ExecuteNonQuery();
    }

    // ------------------------------------------------------------------ tax rate

    /// <summary>
    /// Pure validation core of the C2G 0x012 tax-rate change.
    /// Returns null when allowed, otherwise the refusal error to send.
    /// </summary>
    internal static ErrorMessageType? ValidateTaxRateChange(Dominion dominion, uint? senderExpeditionId,
        bool senderHasDeclarePolicy, int taxRate)
    {
        if (dominion == null)
            return ErrorMessageType.SiegeDeclareBadZone; // no such dominion
        if (senderExpeditionId == null || senderExpeditionId != dominion.ExpeditionId)
            return ErrorMessageType.DominionNotInExpedition; // sender's expedition does not own it
        if (!senderHasDeclarePolicy)
            return ErrorMessageType.SiegeMasterOnly; // owner expedition, but role lacks dominion_declare
        if (taxRate < 0 || taxRate > MaxTaxRate)
            return ErrorMessageType.InvalidTaxation;
        return null;
    }

    public bool ChangeTaxRate(Character character, ushort dominionId, int taxRate, out ErrorMessageType error)
    {
        error = default;
        var dominion = GetDominion(dominionId);

        var hasPolicy = false;
        uint? expeditionId = null;
        if (character?.Expedition != null)
        {
            expeditionId = (uint)character.Expedition.Id;
            var member = character.Expedition.GetMember(character);
            hasPolicy = member != null && character.Expedition.GetPolicyByRole(member.Role)?.DominionDeclare == true;
        }

        var refusal = ValidateTaxRateChange(dominion, expeditionId, hasPolicy, taxRate);
        if (refusal != null)
        {
            error = refusal.Value;
            return false;
        }

        dominion.TaxRate = taxRate;
        PersistDominion(dominion);

        worldManager.BroadcastPacketToServer(new SCDominionTaxRatePacket(dominionId, taxRate));
        Logger.Info("Dominion tax rate changed: zone_group {0} -> {1}% by {2}",
            dominionId, taxRate, character?.Name);
        return true;
    }

    // ------------------------------------------------------------- rebroadcast

    public void SendDominions(GameConnection connection)
    {
        foreach (var dominion in Dominions)
        {
            connection.SendPacket(new SCDominionDataPacket(
                BuildDominionData(dominion.ZoneGroupId, dominion.ExpeditionId, 0, 0f, 0f, 0f,
                    dominion.TaxRate, dominion.DeclaredAt),
                false, true));
        }
    }

    // ------------------------------------------------------------ schedule cron

    /// <summary>
    /// The siege Sunday of the cycle containing <paramref name="utcNow"/> for the given
    /// zone's schedule (declaration evening through payoff moment), or null when
    /// utcNow sits outside any cycle window (mid-week peace).
    /// </summary>
    public DateTime? FindCycleSiegeSunday(SiegeZoneTemplate zone, DateTime utcNow)
    {
        // Candidate cycles: the Sunday starting utcNow's week and the next one.
        var weekSunday = utcNow.Date.AddDays(-((int)utcNow.DayOfWeek + 7) % 7);
        foreach (var candidate in new[] { weekSunday, weekSunday.AddDays(7) })
        {
            var (declareStart, _, _, siegeEnd, payoff) = zone.Anchors(candidate);
            var windowEnd = siegeEnd > payoff ? siegeEnd : payoff;
            if (utcNow >= declareStart && utcNow <= windowEnd)
                return candidate;
        }

        return null;
    }

    public DateTime CurrentCycleSiegeSunday(DateTime utcNow)
    {
        var zone = _siegeZones.Values.FirstOrDefault();
        if (zone == null)
            return utcNow.Date.AddDays(-((int)utcNow.DayOfWeek + 7) % 7);
        return FindCycleSiegeSunday(zone, utcNow) ?? DateTime.MinValue;
    }

    /// <summary>
    /// Zone groups scheduled for the cycle whose siege Sunday is <paramref name="siegeSunday"/>,
    /// derived from the siege_plans rotation (period = distinct week starts).
    /// With no plan rows every zone is considered weekly-active (data-missing fallback).
    /// </summary>
    public HashSet<uint> ActiveZoneGroupsForWeek(DateTime siegeSunday)
    {
        if (_planWeekStarts.Count == 0)
            return [.. _siegeZones.Keys];

        var first = _planWeekStarts[0];
        var weeksSinceFirst = (int)Math.Floor((siegeSunday.Date - first).TotalDays / 7.0);
        var index = ((weeksSinceFirst % _planWeekStarts.Count) + _planWeekStarts.Count) % _planWeekStarts.Count;
        var target = first.AddDays(index * 7);

        return [.. _siegePlans.Where(p => p.WeekStart.Date == target).Select(p => p.ZoneGroupId)];
    }

    public SiegePhase GetCurrentPhase(SiegeZoneTemplate zone, DateTime utcNow)
    {
        var sunday = FindCycleSiegeSunday(zone, utcNow);
        if (sunday == null)
            return SiegePhase.Peace;

        if (!ActiveZoneGroupsForWeek(sunday.Value).Contains(zone.ZoneGroupId))
            return SiegePhase.Peace; // rotation: this castle has no siege this cycle

        var (declareStart, warmupStart, siegeStart, siegeEnd, _) = zone.Anchors(sunday.Value);

        if (utcNow >= warmupStart && utcNow < siegeStart)
            return SiegePhase.Warmup;
        if (utcNow >= siegeStart && utcNow < siegeEnd)
            return SiegePhase.Siege;
        if (utcNow >= declareStart && utcNow < warmupStart)
            return SiegePhase.Declare;

        return SiegePhase.Peace; // post-battle lull and after payoff
    }

    /// <summary>Announces phase transitions and the payoff moment for every siege zone.</summary>
    private void ScheduleTick(TimeSpan delta)
    {
        var now = DateTime.UtcNow;
        try
        {
            foreach (var zone in _siegeZones.Values)
            {
                var phase = GetCurrentPhase(zone, now);

                var firstSighting = !_lastAnnouncedPhase.TryGetValue(zone.ZoneGroupId, out var previous);
                if (firstSighting || previous != phase)
                {
                    _lastAnnouncedPhase[zone.ZoneGroupId] = phase;
                    Logger.Info("Siege phase: zone_group {0} (template {1}) -> {2}", zone.ZoneGroupId, zone.Id, phase);
                    if (!firstSighting || phase != SiegePhase.Peace)
                        worldManager.BroadcastPacketToServer(new SCSiegeAlertPacket((ushort)zone.ZoneGroupId, (byte)phase));
                }

                // Payoff fires as a point event exactly once per cycle.
                var sunday = FindCycleSiegeSunday(zone, now);
                if (sunday != null)
                {
                    var (_, _, _, _, payoffMoment) = zone.Anchors(sunday.Value);
                    if (_lastTickUtc < payoffMoment && payoffMoment <= now && _announcedPayoffs.Add(payoffMoment))
                    {
                        Logger.Info("Siege phase: zone_group {0} (template {1}) -> Payoff", zone.ZoneGroupId, zone.Id);
                        worldManager.BroadcastPacketToServer(new SCSiegeAlertPacket((ushort)zone.ZoneGroupId, (byte)SiegePhase.Payoff));
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error(ex, "DominionManager.ScheduleTick");
        }
        finally
        {
            _lastTickUtc = now;
        }
    }
}
