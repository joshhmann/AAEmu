using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// Plan T9 (audit L3-a "dispatcher-or-deny"): the ONE declaration of who
/// carries which elected activity. These tests pin the resolution the
/// executor's dispatch gates and the arbiter's refusal both read, including the
/// names that deliberately have no carrier.
/// </summary>
public class BotActivityDispatchSurfaceTests
{
    [Test]
    public async Task Resolve_MapsEveryCarriedFamily_ToItsCarrier()
    {
        await Assert.That(BotActivityDispatchSurface.Resolve("conflict.5")).IsEqualTo(BotActivityCarrier.Conflict);
        await Assert.That(BotActivityDispatchSurface.Resolve("needs.farm")).IsEqualTo(BotActivityCarrier.Needs);
        await Assert.That(BotActivityDispatchSurface.Resolve("quest.progress")).IsEqualTo(BotActivityCarrier.Quest);
        await Assert.That(BotActivityDispatchSurface.Resolve("quest.discovery")).IsEqualTo(BotActivityCarrier.Quest);
        await Assert.That(BotActivityDispatchSurface.Resolve("homestead.progression")).IsEqualTo(BotActivityCarrier.Homestead);
        await Assert.That(BotActivityDispatchSurface.Resolve("recovery.rest")).IsEqualTo(BotActivityCarrier.Recovery);
        await Assert.That(BotActivityDispatchSurface.Resolve("recovery.consume")).IsEqualTo(BotActivityCarrier.Recovery);
        await Assert.That(BotActivityDispatchSurface.Resolve("assigned.travel")).IsEqualTo(BotActivityCarrier.AssignedTravel);
        await Assert.That(BotActivityDispatchSurface.Resolve("schedule.work")).IsEqualTo(BotActivityCarrier.Schedule);
        await Assert.That(BotActivityDispatchSurface.Resolve("presence.roam")).IsEqualTo(BotActivityCarrier.PresenceRoam);
        await Assert.That(BotActivityDispatchSurface.Resolve("idle")).IsEqualTo(BotActivityCarrier.Idle);
    }

    [Test]
    public async Task Resolve_ActivityNobodyPerforms_HasNoCarrier()
    {
        await Assert.That(BotActivityDispatchSurface.Resolve("fishing.contest")).IsNull();
        await Assert.That(BotActivityDispatchSurface.Resolve("adventure.explore")).IsNull();
        await Assert.That(BotActivityDispatchSurface.Resolve("")).IsNull();
        await Assert.That(BotActivityDispatchSurface.Resolve(null)).IsNull();
    }

    [Test]
    public async Task IsCarried_AnswersTheArbitersQuestion()
    {
        await Assert.That(BotActivityDispatchSurface.IsCarried("needs.farm")).IsTrue();
        await Assert.That(BotActivityDispatchSurface.IsCarried("idle")).IsTrue();
        await Assert.That(BotActivityDispatchSurface.IsCarried("fishing.contest")).IsFalse();
        await Assert.That(BotActivityDispatchSurface.IsCarried(null)).IsFalse();
    }

    [Test]
    public async Task DeclaredRules_CoverEveryCarrier_AndSayWhoCarriesIt()
    {
        var rules = BotActivityDispatchSurface.DeclaredRules;

        await Assert.That(rules.Count).IsEqualTo(11);
        await Assert.That(rules.Select(rule => rule.Carrier).Distinct().Count()).IsEqualTo(10);
        await Assert.That(rules.All(rule => !string.IsNullOrWhiteSpace(rule.Value))).IsTrue();
        await Assert.That(rules.All(rule => !string.IsNullOrWhiteSpace(rule.CarriedBy))).IsTrue();
    }
}
