namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

using AAEmu.Game.Core.Managers.Bots;

/// <summary>
/// Pure 24h-sweep + boundary-hysteresis rig for the C1 schedule phase
/// resolver (template anchors: work 08-18, rest 22-06 wrap, home by 20;
/// hysteresis 1/6 game-hour; travel legs 0.5 game-hour).
/// </summary>
public class BotScheduleResolverTests
{
    private const float H = BotScheduleResolver.DefaultHysteresisHours;

    // ---------------------------------------------------------------- first resolution (no previous phase)

    [Test]
    public async Task Resolve_FirstResolution_Night_IsRest()
    {
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 0f, null))
            .IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 23f, null))
            .IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 5.9f, null))
            .IsEqualTo(BotSchedulePhase.Rest);
    }

    [Test]
    public async Task Resolve_FirstResolution_Midday_IsWork()
    {
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 12f, null))
            .IsEqualTo(BotSchedulePhase.Work);
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 8.5f, null))
            .IsEqualTo(BotSchedulePhase.Work);
    }

    // ---------------------------------------------------------------- full 24h sweep (chained, step 15 game-min)

    [Test]
    public async Task Resolve_FullDaySweep_ProducesTheExpectedPhaseSequence()
    {
        BotSchedulePhase? current = null;
        for (var quarter = 0; quarter < 96; quarter++)
        {
            var hour = quarter * 0.25f;
            current = BotScheduleResolver.Resolve(BotDailyAnchors.Template, hour, current);
            await Assert.That(current).IsEqualTo(ExpectedTemplatePhase(hour));
        }
    }

    /// <summary>
    /// The hand-derived steady-state sequence for the template schedule
    /// (hysteresis holds included): Rest → 06.25 Home → 07.5 Travel →
    /// 08.25 Work → 18.25 Travel → 20.0 Home → 22.25 Rest.
    /// </summary>
    private static BotSchedulePhase ExpectedTemplatePhase(float hour) => hour switch
    {
        < 6.25f => BotSchedulePhase.Rest,
        < 7.5f => BotSchedulePhase.Home,
        < 8.25f => BotSchedulePhase.Travel,
        < 18.25f => BotSchedulePhase.Work,
        < 20f => BotSchedulePhase.Travel,
        < 22.25f => BotSchedulePhase.Home,
        _ => BotSchedulePhase.Rest
    };

    // ---------------------------------------------------------------- boundary hysteresis

    [Test]
    public async Task Resolve_WorkEndBoundary_HoldsWork_InsideHysteresisWindow()
    {
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 18.01f, BotSchedulePhase.Work))
            .IsEqualTo(BotSchedulePhase.Work);
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 18.16f, BotSchedulePhase.Work))
            .IsEqualTo(BotSchedulePhase.Work);
    }

    [Test]
    public async Task Resolve_WorkEndBoundary_SwitchesPastHysteresis_IntoEveningTravel()
    {
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 18.2f, BotSchedulePhase.Work))
            .IsEqualTo(BotSchedulePhase.Travel);
    }

    [Test]
    public async Task Resolve_OscillatingAroundBoundary_NeverFlaps()
    {
        // A clock jittering around WorkEnd must hold Work forever.
        var phase = BotSchedulePhase.Work;
        for (var i = 0; i < 20; i++)
        {
            phase = BotScheduleResolver.Resolve(BotDailyAnchors.Template, 17.99f + (i % 2) * 0.02f, phase);
            await Assert.That(phase).IsEqualTo(BotSchedulePhase.Work);
        }
    }

    [Test]
    public async Task Resolve_RestStartBoundary_HoldsHomeThenSwitchesToRest()
    {
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 22.01f, BotSchedulePhase.Home))
            .IsEqualTo(BotSchedulePhase.Home);
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 22.2f, BotSchedulePhase.Home))
            .IsEqualTo(BotSchedulePhase.Rest);
    }

    [Test]
    public async Task Resolve_WorkStartBoundary_HoldsTravelThroughTheBoundary()
    {
        // Morning Travel is held into the Work window until the hysteresis passes.
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 8.05f, BotSchedulePhase.Travel))
            .IsEqualTo(BotSchedulePhase.Travel);
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 8.2f, BotSchedulePhase.Travel))
            .IsEqualTo(BotSchedulePhase.Work);
    }

    // ---------------------------------------------------------------- travel windows

    [Test]
    public async Task Resolve_MorningTravelLeg_OnlyInsideHalfHourBeforeWork()
    {
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 7.4f, BotSchedulePhase.Home))
            .IsEqualTo(BotSchedulePhase.Home);
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 7.6f, BotSchedulePhase.Home))
            .IsEqualTo(BotSchedulePhase.Travel);
    }

    [Test]
    public async Task Resolve_EveningTravelLeg_EndsAtHomeBy()
    {
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 19.9f, BotSchedulePhase.Travel))
            .IsEqualTo(BotSchedulePhase.Travel); // still before HomeBy=20
        await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, 20.1f, BotSchedulePhase.Home))
            .IsEqualTo(BotSchedulePhase.Home);
    }

    [Test]
    public async Task IsMorningTravel_DistinguishesLegDestinations()
    {
        await Assert.That(BotScheduleResolver.IsMorningTravel(BotDailyAnchors.Template, 7.75f,
                BotScheduleResolver.DefaultTravelDurationHours)).IsTrue();
        await Assert.That(BotScheduleResolver.IsMorningTravel(BotDailyAnchors.Template, 19f,
                BotScheduleResolver.DefaultTravelDurationHours)).IsFalse();
    }

    // ---------------------------------------------------------------- custom anchors + wrap handling

    [Test]
    public async Task Resolve_CustomNightShiftAnchors_FollowsThem()
    {
        var nightShift = new BotDailyAnchors
        {
            WorkStart = 22f, WorkEnd = 6f, RestStart = 10f, RestEnd = 14f, HomeBy = 9f
        };

        await Assert.That(nightShift.IsValid).IsTrue(); // work wraps midnight
        await Assert.That(BotScheduleResolver.Resolve(nightShift, 23f, null)).IsEqualTo(BotSchedulePhase.Work);
        await Assert.That(BotScheduleResolver.Resolve(nightShift, 3f, BotSchedulePhase.Work)).IsEqualTo(BotSchedulePhase.Work);
        await Assert.That(BotScheduleResolver.Resolve(nightShift, 12f, null)).IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(BotScheduleResolver.Resolve(nightShift, 16f, null)).IsEqualTo(BotSchedulePhase.Home);
    }

    [Test]
    public async Task BasePhase_MatchesResolverSteadyState()
    {
        foreach (var hour in new[] { 0f, 7f, 12f, 16f, 21.5f, 23.75f })
        {
            var basePhase = BotScheduleResolver.BasePhase(BotDailyAnchors.Template, hour);
            // Away from boundaries the resolver must agree with its base phase.
            if (basePhase is not (BotSchedulePhase.Home or BotSchedulePhase.Travel))
                await Assert.That(BotScheduleResolver.Resolve(BotDailyAnchors.Template, hour, basePhase))
                    .IsEqualTo(basePhase);
        }

        await Assert.That(BotScheduleResolver.BasePhase(BotDailyAnchors.Template, 12f)).IsEqualTo(BotSchedulePhase.Work);
        await Assert.That(BotScheduleResolver.BasePhase(BotDailyAnchors.Template, 1f)).IsEqualTo(BotSchedulePhase.Rest);
        await Assert.That(BotScheduleResolver.BasePhase(BotDailyAnchors.Template, 7f)).IsEqualTo(BotSchedulePhase.Home);
    }
}
