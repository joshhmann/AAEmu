using System;

using AAEmu.Game.Core.Managers;
using AAEmu.Game.Models.Game;
using AAEmu.Game.Models.Game.Siege;
using AAEmu.Game.Utils.DB;

using Microsoft.Data.Sqlite;

namespace AAEmu.UnitTests.Game.Core.Managers;

/// <summary>
/// DominionManager slice-1 unit tests: siege template loading from a synthetic
/// compact-shaped sqlite fixture, the weekly rotation index math, the
/// Peace→Declare→Warmup→Siege→Payoff phase computation against the real shipped
/// schedule shape (declare Sat 22:30 → warmup Sun 20:30 → siege Sun 21:00 +1h30m
/// → payoff Sun 23:55), and the tax-rate change validation refusals.
/// </summary>
public class DominionScheduleTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly DominionManager _manager;

    public DominionScheduleTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        using (var cmd = _connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE siege_zones (
                    id INT PRIMARY KEY,
                    start_siege_weekday INT, start_siege_hour INT, start_siege_min INT,
                    siege_days INT, siege_hours INT, siege_mins INT,
                    zone_group_id INT,
                    pay_weekday INT, pay_hour INT, pay_min INT,
                    declare_item_id INT, defense_ticket_id INT, offense_ticket_id INT,
                    reinforce_defense_delay_mins INT,
                    defense_merchant_id INT, offense_merchant_id INT, dominion_merchant_id INT,
                    open_hour INT, open_duration_hours INT,
                    start_auction_weekday INT, start_auction_hour INT, start_auction_min INT,
                    start_declare_weekday INT, start_declare_hour INT, start_declare_min INT,
                    start_warmup_weekday INT, start_warmup_hour INT, start_warmup_min INT,
                    open_weekday INT,
                    monument_doodad_id INT);

                -- Mirrors the canonical Salpimari row (siege_zones id=1)
                INSERT INTO siege_zones VALUES (
                    1, 0, 21, 0, 0, 1, 30, 33, 0, 23, 55,
                    21134, 21314, 21318, 20, 12629, 51, 50,
                    0, 168, 3, 22, 30, 5, 22, 30, 0, 20, 30, 0, 7229);
                -- Second zone with a shifted group id for rotation tests
                INSERT INTO siege_zones VALUES (
                    2, 0, 21, 0, 0, 1, 30, 34, 0, 23, 55,
                    21130, 21313, 21317, 20, 12630, 51, 50,
                    0, 168, 3, 22, 30, 5, 22, 30, 0, 20, 30, 0, 7230);

                CREATE TABLE siege_settings (
                    total_castles INT, num_defenders INT, num_reinforcements INT);
                -- Mirrors the canonical shape: (slot 0: 70 defenders / 0 reinforc.), (slot 1: 50 / 20)
                INSERT INTO siege_settings VALUES (0, 70, 0), (1, 50, 20);
                CREATE TABLE siege_plans (id INT PRIMARY KEY, zone_group_id INT, week_start TEXT);
                -- A two-week rotation: week A -> 33, week B -> 34
                INSERT INTO siege_plans VALUES (1, 33, '2014-01-05 00:00:00');
                INSERT INTO siege_plans VALUES (2, 34, '2014-01-12 00:00:00');
                """;
            cmd.ExecuteNonQuery();
        }

        _manager = new DominionManager(null!, null!, null!);
        _manager.LoadSiegeTemplates(_connection);


        // Phase/cycle tests use a plans-free manager: without siege_plans rows every
        // zone counts as weekly-active, so rotation never masks the schedule math.
        _phaseConnection = new SqliteConnection("Data Source=:memory:");
        _phaseConnection.Open();
        using (var cmd = _phaseConnection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE siege_zones (
                    id INT PRIMARY KEY,
                    start_siege_weekday INT, start_siege_hour INT, start_siege_min INT,
                    siege_days INT, siege_hours INT, siege_mins INT,
                    zone_group_id INT,
                    pay_weekday INT, pay_hour INT, pay_min INT,
                    declare_item_id INT, defense_ticket_id INT, offense_ticket_id INT,
                    reinforce_defense_delay_mins INT,
                    defense_merchant_id INT, offense_merchant_id INT, dominion_merchant_id INT,
                    open_hour INT, open_duration_hours INT,
                    start_auction_weekday INT, start_auction_hour INT, start_auction_min INT,
                    start_declare_weekday INT, start_declare_hour INT, start_declare_min INT,
                    start_warmup_weekday INT, start_warmup_hour INT, start_warmup_min INT,
                    open_weekday INT,
                    monument_doodad_id INT);
                INSERT INTO siege_zones VALUES (
                    1, 0, 21, 0, 0, 1, 30, 33, 0, 23, 55,
                    21134, 21314, 21318, 20, 12629, 51, 50,
                    0, 168, 3, 22, 30, 5, 22, 30, 0, 20, 30, 0, 7229);
                CREATE TABLE siege_settings (
                    total_castles INT, num_defenders INT, num_reinforcements INT);
                """;
            cmd.ExecuteNonQuery();
        }

        _phaseManager = new DominionManager(null!, null!, null!);
        _phaseManager.LoadSiegeTemplates(_phaseConnection);

    }
    private readonly SqliteConnection _phaseConnection;
    private readonly DominionManager _phaseManager;

    public void Dispose()
    {
        _connection.Dispose();
        _phaseConnection.Dispose();
    }

    // ------------------------------------------------------------------ load

    [Test]
    public async Task Load_ParsesSiegeZones()
    {
        await Assert.That(_manager.SiegeZones.Count).IsEqualTo(2);
        var salpimari = _manager.GetSiegeZoneByGroup(33);
        await Assert.That(salpimari).IsNotNull();
        await Assert.That(salpimari!.StartSiegeHour).IsEqualTo(21);
        await Assert.That(salpimari.SiegeDuration).IsEqualTo(TimeSpan.FromMinutes(90));
        await Assert.That(salpimari.DeclareItemId).IsEqualTo(21134u);
        await Assert.That(salpimari.MonumentDoodadId).IsEqualTo(7229u);
    }

    [Test]
    public async Task Load_ParsesSiegeSettings()
    {
        await Assert.That(_manager.SiegeSettings.Count).IsEqualTo(2);
        await Assert.That(_manager.SiegeSettings[0].NumDefenders).IsEqualTo(70);
        await Assert.That(_manager.SiegeSettings[0].NumReinforcements).IsEqualTo(0);
        await Assert.That(_manager.SiegeSettings[1].NumDefenders).IsEqualTo(50);
        await Assert.That(_manager.SiegeSettings[1].NumReinforcements).IsEqualTo(20);
    }

    [Test]
    public async Task Load_ParsesSiegePlansRotation()
    {
        // Week A (2014-01-05) -> zone group 33, week B (2014-01-12) -> 34
        var weekA = _manager.ActiveZoneGroupsForWeek(new DateTime(2014, 1, 5));
        var weekB = _manager.ActiveZoneGroupsForWeek(new DateTime(2014, 1, 12));
        var weekA2 = _manager.ActiveZoneGroupsForWeek(new DateTime(2014, 1, 5).AddDays(14)); // wraps

        await Assert.That(weekA.Count).IsEqualTo(1);
        await Assert.That(weekA).Contains(33u);
        await Assert.That(weekB.Count).IsEqualTo(1);
        await Assert.That(weekB).Contains(34u);
        await Assert.That(weekA2).Contains(33u); // period-2 rotation repeats
    }

    [Test]
    public async Task Rotation_ArbitraryLaterSunday_ResolvesInsideRotationPeriod()
    {
        var groups = _manager.ActiveZoneGroupsForWeek(new DateTime(2026, 8, 23)); // a Sunday
        await Assert.That(groups.Count).IsEqualTo(1);
    }

    // ------------------------------------------------------------------ phases

    private static SiegeZoneTemplate RealShapeZone() => new()
    {
        Id = 1,
        ZoneGroupId = 33,
        StartSiegeWeekday = 0, StartSiegeHour = 21, StartSiegeMin = 0,
        SiegeDays = 0, SiegeHours = 1, SiegeMins = 30,
        PayWeekday = 0, PayHour = 23, PayMin = 55,
        StartDeclareWeekday = 5, StartDeclareHour = 22, StartDeclareMin = 30,
        StartWarmupWeekday = 0, StartWarmupHour = 20, StartWarmupMin = 30
    };

    [Test]
    public async Task Phase_DeclareWindow_SaturdayEvening()
    {
        var zone = RealShapeZone();
        // Saturday 2026-08-29 23:00 UTC is inside declare (Fri 22:30 → Sun 20:30)
        var phase = _phaseManager.GetCurrentPhase(zone, new DateTime(2026, 8, 29, 23, 0, 0));
        await Assert.That(phase).IsEqualTo(SiegePhase.Declare);
    }

    [Test]
    public async Task Phase_Warmup_SundayBeforeBattle()
    {
        var zone = RealShapeZone();
        var phase = _phaseManager.GetCurrentPhase(zone, new DateTime(2026, 8, 30, 20, 45, 0));
        await Assert.That(phase).IsEqualTo(SiegePhase.Warmup);
    }

    [Test]
    public async Task Phase_Siege_SundayNightWithDuration()
    {
        var zone = RealShapeZone();
        await Assert.That(
            _phaseManager.GetCurrentPhase(zone, new DateTime(2026, 8, 30, 21, 30, 0))).IsEqualTo(SiegePhase.Siege);
        // Siege ends at 22:30 — after that the interval phase returns to Peace.
        await Assert.That(
            _phaseManager.GetCurrentPhase(zone, new DateTime(2026, 8, 30, 22, 31, 0))).IsEqualTo(SiegePhase.Peace);
    }

    [Test]
    public async Task Phase_MidWeek_IsPeace()
    {
        var zone = RealShapeZone();
        var phase = _phaseManager.GetCurrentPhase(zone, new DateTime(2026, 8, 26, 12, 0, 0)); // Wednesday
        await Assert.That(phase).IsEqualTo(SiegePhase.Peace);
    }

    [Test]
    public async Task CycleSunday_ResolvesDeclarationWeekendCorrectly()
    {
        var zone = RealShapeZone();
        // During Saturday evening's declare window the cycle's siege Sunday is tomorrow.
        var sunday = _phaseManager.FindCycleSiegeSunday(zone, new DateTime(2026, 8, 29, 23, 0, 0));
        await Assert.That(sunday).IsEqualTo(new DateTime(2026, 8, 30));

        // Mid-week there is no active cycle window.
        await Assert.That(
            _phaseManager.FindCycleSiegeSunday(zone, new DateTime(2026, 8, 26, 12, 0, 0))).IsNull();
    }

    // ------------------------------------------------------------------ tax rate policy

    private static Dominion OwnedDominion() => new()
    { ZoneGroupId = 33, ExpeditionId = 7, ExpeditionName = "owners", TaxRate = 50, DeclaredAt = DateTime.UtcNow };

    [Test]
    public async Task TaxRate_NoDominion_IsRefused()
    {
        var error = DominionManager.ValidateTaxRateChange(null!, 7, true, 10);
        await Assert.That(error).IsEqualTo(ErrorMessageType.SiegeDeclareBadZone);
    }

    [Test]
    public async Task TaxRate_NotInExpedition_IsRefused()
    {
        var error = DominionManager.ValidateTaxRateChange(OwnedDominion(), null, false, 10);
        await Assert.That(error).IsEqualTo(ErrorMessageType.DominionNotInExpedition);
    }

    [Test]
    public async Task TaxRate_WrongExpedition_IsRefused()
    {
        var error = DominionManager.ValidateTaxRateChange(OwnedDominion(), 99, true, 10);
        await Assert.That(error).IsEqualTo(ErrorMessageType.DominionNotInExpedition);
    }

    [Test]
    public async Task TaxRate_MissingDeclarePolicy_IsRefused()
    {
        var error = DominionManager.ValidateTaxRateChange(OwnedDominion(), 7, false, 10);
        await Assert.That(error).IsEqualTo(ErrorMessageType.SiegeMasterOnly);
    }

    [Test]
    public async Task TaxRate_OutOfRange_IsRefused()
    {
        await Assert.That(DominionManager.ValidateTaxRateChange(OwnedDominion(), 7, true, -1))
            .IsEqualTo(ErrorMessageType.InvalidTaxation);
        await Assert.That(DominionManager.ValidateTaxRateChange(OwnedDominion(), 7, true, DominionManager.MaxTaxRate + 1))
            .IsEqualTo(ErrorMessageType.InvalidTaxation);
    }

    [Test]
    public async Task TaxRate_OwningExpeditionWithPolicy_IsAllowed()
    {
        var error = DominionManager.ValidateTaxRateChange(OwnedDominion(), 7, true, 10);
        await Assert.That(error).IsNull();
    }
}

/// <summary>
/// Loads the REAL shipped schedule data out of the canonical compact.sqlite3
/// (gitignored; soft-skips when absent) through the production loader path.
/// </summary>
public class DominionRealDataTests : IDisposable
{
    private readonly SqliteConnection? _connection;
    private readonly DominionManager? _manager;
    private readonly bool _dataPresent;

    public DominionRealDataTests()
    {
        var dbPath = Path.Combine(AAEmu.Commons.IO.FileManager.AppPath, "Data", "compact.sqlite3");
        if (!File.Exists(dbPath))
            return;

        _dataPresent = true;
        _connection = SQLite.CreateConnection();
        _manager = new DominionManager(null!, null!, null!);
        _manager.LoadSiegeTemplates(_connection!);
    }

    public void Dispose() => _connection?.Dispose();

    [Test]
    public async Task RealData_ShipsSixSiegeZonesWithSchedules()
    {
        if (!_dataPresent)
        {
            Console.WriteLine("[DominionRealData] SKIPPED — canonical compact.sqlite3 not present");
            return;
        }

        await Assert.That(_manager!.SiegeZones.Count).IsEqualTo(6);
        foreach (var zone in _manager.SiegeZones)
        {
            await Assert.That(zone.ZoneGroupId > 0u).IsTrue();
            await Assert.That(zone.StartSiegeWeekday >= 0 && zone.StartSiegeWeekday <= 6).IsTrue();
            await Assert.That(zone.StartSiegeHour >= 0 && zone.StartSiegeHour <= 23).IsTrue();
            await Assert.That(zone.MonumentDoodadId > 0u).IsTrue();
        }
    }

    [Test]
    public async Task RealData_ShipsSettingsAndPlanRotation()
    {
        if (!_dataPresent)
            return; // skipped — see RealData_ShipsSixSiegeZonesWithSchedules

        await Assert.That(_manager!.SiegeSettings.Count).IsEqualTo(11);
        // Legacy 2014-dated weekly rotation ships 158 rows over 52 distinct weeks.
        await Assert.That(_manager.SiegePlans.Count).IsEqualTo(158);

        // Every shipped cycle Sunday resolves to a non-empty scheduled set.
        var groups = _manager.ActiveZoneGroupsForWeek(new DateTime(2026, 8, 30));
        await Assert.That(groups.Count > 0).IsTrue();
    }

    [Test]
    public async Task RealData_AllSixZoneGroupsResolve()
    {
        if (!_dataPresent)
            return;

        var known = _manager!.SiegeZones.Select(z => z.ZoneGroupId).Order().ToArray();
        await Assert.That(known).IsEquivalentTo(new uint[] { 33, 34, 43, 44, 54, 56 });
    }
}
