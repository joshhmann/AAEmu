using System;

namespace AAEmu.Game.Models.Game.Siege;

/// <summary>
/// Template for one castle/dominion zone loaded from the <c>siege_zones</c> table
/// of compact.sqlite3 (read-only reference data).
/// Weekday columns use the client convention: 0 = Sunday … 6 = Saturday.
/// </summary>
public class SiegeZoneTemplate
{
    public uint Id { get; set; }
    public uint ZoneGroupId { get; set; }

    // Siege battle start + duration
    public int StartSiegeWeekday { get; set; }
    public int StartSiegeHour { get; set; }
    public int StartSiegeMin { get; set; }
    public int SiegeDays { get; set; }
    public int SiegeHours { get; set; }
    public int SiegeMins { get; set; }

    // Tax payoff moment
    public int PayWeekday { get; set; }
    public int PayHour { get; set; }
    public int PayMin { get; set; }

    // Items
    public uint DeclareItemId { get; set; }
    public uint DefenseTicketId { get; set; }
    public uint OffenseTicketId { get; set; }
    public int ReinforceDefenseDelayMins { get; set; }
    public uint DefenseMerchantId { get; set; }
    public uint OffenseMerchantId { get; set; }
    public uint DominionMerchantId { get; set; }

    // Open period
    public int OpenHour { get; set; }
    public int OpenDurationHours { get; set; }
    public int OpenWeekday { get; set; }

    // Auction window
    public int StartAuctionWeekday { get; set; }
    public int StartAuctionHour { get; set; }
    public int StartAuctionMin { get; set; }

    // Declaration window
    public int StartDeclareWeekday { get; set; }
    public int StartDeclareHour { get; set; }
    public int StartDeclareMin { get; set; }

    // Warmup window
    public int StartWarmupWeekday { get; set; }
    public int StartWarmupHour { get; set; }
    public int StartWarmupMin { get; set; }

    public uint MonumentDoodadId { get; set; }

    /// <summary>Duration of the siege battle phase.</summary>
    public TimeSpan SiegeDuration => new(SiegeDays, SiegeHours, SiegeMins, 0);

    /// <summary>
    /// Resolves one weekly schedule column to a wall-clock moment inside the cycle
    /// whose siege battle day is <paramref name="cycleSunday"/>. Weekday columns are
    /// numbered 0 = Sunday … 6 = Saturday and are interpreted as "the most recent
    /// such weekday on or before the siege Sunday" (e.g. start_declare_weekday=5 →
    /// the Friday two days before the battle Sunday).
    /// </summary>
    private static DateTime On(DateTime cycleSunday, int weekday, int hour, int minute)
    {
        var daysBack = (7 - weekday % 7) % 7;
        return cycleSunday.Date.AddDays(-daysBack).AddHours(hour).AddMinutes(minute);
    }

    /// <summary>
    /// Computes the schedule anchors of the cycle whose siege Sunday is
    /// <paramref name="cycleSunday"/>.
    /// </summary>
    public (DateTime DeclareStart, DateTime WarmupStart, DateTime SiegeStart, DateTime SiegeEnd, DateTime PayoffMoment) Anchors(DateTime cycleSunday)
    {
        var declareStart = On(cycleSunday, StartDeclareWeekday, StartDeclareHour, StartDeclareMin);
        var warmupStart = On(cycleSunday, StartWarmupWeekday, StartWarmupHour, StartWarmupMin);
        var siegeStart = On(cycleSunday, StartSiegeWeekday, StartSiegeHour, StartSiegeMin);
        var siegeEnd = siegeStart + SiegeDuration;
        var payoff = On(cycleSunday, PayWeekday, PayHour, PayMin);
        return (declareStart, warmupStart, siegeStart, siegeEnd, payoff);
    }
}
