using AAEmu.Commons.Utils.Gate;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Window/window-edge unit tests for warmup-blind soak evaluation: budget
/// events inside any boot's [boot−30s, boot+120s] window are recorded, never
/// counted. Pure time math — no live stack needed.
/// </summary>
public class SoakWarmupTests
{
    private static readonly DateTime Boot = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

    [Test]
    public async Task IsWarmup_EventAfterBootInsideLag_ReturnsTrue()
    {
        // Forensics band: region overruns at +3..+17s, physics clusters at
        // +2..+67s — all blind.
        await Assert.That(SoakWarmup.IsWarmup(Boot.AddSeconds(17), [Boot])).IsTrue();
        await Assert.That(SoakWarmup.IsWarmup(Boot.AddSeconds(67), [Boot])).IsTrue();
    }

    [Test]
    public async Task IsWarmup_EventBeforeBootInsideLead_ReturnsTrue()
    {
        await Assert.That(SoakWarmup.IsWarmup(Boot.AddSeconds(-29), [Boot])).IsTrue();
    }

    [Test]
    public async Task IsWarmup_EventOutsideWindow_ReturnsFalse()
    {
        await Assert.That(SoakWarmup.IsWarmup(Boot.AddSeconds(-31), [Boot])).IsFalse();
        await Assert.That(SoakWarmup.IsWarmup(Boot.AddSeconds(121), [Boot])).IsFalse();
        await Assert.That(SoakWarmup.IsWarmup(Boot.AddHours(2), [Boot])).IsFalse();
    }

    [Test]
    public async Task IsWarmup_EventOnWindowEdge_IsBlind()
    {
        // Inclusive on both edges: exactly −30s and +120s are warmup.
        await Assert.That(SoakWarmup.IsWarmup(Boot.AddSeconds(-30), [Boot])).IsTrue();
        await Assert.That(SoakWarmup.IsWarmup(Boot.AddSeconds(120), [Boot])).IsTrue();
    }

    [Test]
    public async Task IsWarmup_EventNearSecondBoot_BlindedByThatBoot()
    {
        var boots = new List<DateTime> { Boot, Boot.AddSeconds(140) };
        // +130s after the first boot is steady-state for boot 1 but −10s
        // for boot 2 — blind.
        await Assert.That(SoakWarmup.IsWarmup(Boot.AddSeconds(130), boots)).IsTrue();
    }

    [Test]
    public async Task IsWarmup_NoBoots_NeverBlind()
    {
        await Assert.That(SoakWarmup.IsWarmup(Boot, [])).IsFalse();
    }

    [Test]
    public async Task SteadyStateMinutes_NoBoots_FullWindow()
    {
        var minutes = SoakWarmup.SteadyStateMinutes(Boot, Boot.AddMinutes(10), []);
        await Assert.That(minutes).IsEqualTo(10).Within(0.0001);
    }

    [Test]
    public async Task SteadyStateMinutes_BootInsideWindow_SubtractsBlindInterval()
    {
        // 10-min window, boot at +5min blinds [3:30, 7:00] → 2.5 blind.
        var start = Boot.AddMinutes(-5);
        var minutes = SoakWarmup.SteadyStateMinutes(start, start.AddMinutes(10), [Boot]);
        await Assert.That(minutes).IsEqualTo(7.5).Within(0.0001);
    }

    [Test]
    public async Task SteadyStateMinutes_BootBeforeWindow_ClipsLeadOverlap()
    {
        // Boot 60s before window start: only [start−0s, start+60s] overlaps
        // (the [boot−30s, boot+120s] tail) → 9 blind-free minutes of 10.
        var start = Boot.AddSeconds(60);
        var minutes = SoakWarmup.SteadyStateMinutes(start, start.AddMinutes(10), [Boot]);
        await Assert.That(minutes).IsEqualTo(9).Within(0.0001);
    }

    [Test]
    public async Task SteadyStateMinutes_Boots140sApart_OverlappingBlindsUnion()
    {
        // Forensics case: boots ~140s apart — each blinds 150s, overlapping
        // by 10s → 290s blind of a 10-min window.
        var start = Boot.AddSeconds(-30);
        var boots = new List<DateTime> { Boot, Boot.AddSeconds(140) };
        var minutes = SoakWarmup.SteadyStateMinutes(start, start.AddMinutes(10), boots);
        await Assert.That(minutes).IsEqualTo((600 - 290) / 60.0).Within(0.0001);
    }
    [Test]
    public async Task SteadyStateMinutes_FullyCoveredWindow_ReturnsZero()
    {
        // Dense reboots covering the whole window: zero steady-state seconds
        // (verdicts fall back to the wall window; the boots stay on record).
        // Boots every 100s blind 150s each — overlapping chain covers [0,480].
        var start = Boot;
        var boots = new List<DateTime>
        {
            Boot.AddSeconds(30), Boot.AddSeconds(130), Boot.AddSeconds(230),
            Boot.AddSeconds(330), Boot.AddSeconds(430),
        };
        var minutes = SoakWarmup.SteadyStateMinutes(start, start.AddMinutes(8), boots);
        await Assert.That(minutes).IsEqualTo(0).Within(0.0001);
    }

    [Test]
    public async Task SteadyStateMinutes_InvertedWindow_ReturnsZero()
    {
        var minutes = SoakWarmup.SteadyStateMinutes(Boot, Boot, [Boot]);
        await Assert.That(minutes).IsEqualTo(0);
    }
}
