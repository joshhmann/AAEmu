using AAEmu.Game.Core.Managers.Bots;

namespace AAEmu.UnitTests.Game.Core.Managers.Bots;

/// <summary>
/// T2 supported arming — the reconciliation seam behind re-enrollment. When a
/// managed account already owns characters, the enrollment adopts the OLDEST
/// row and leaves the newer duplicates alone; this pins that pick, which the
/// real-service lane can only exercise once (review Q7 gap: rigs pass a null
/// name provider and take the catch path, so the seam had no direct test).
/// </summary>
[NotInParallel]
public class BotPresenceReconciliationTests
{
    [Test]
    public async Task SelectOldestOwned_AccountOwnsNothing_IsNull()
    {
        await Assert.That(BotPresenceCoordinator.SelectOldestOwned([])).IsNull();
    }

    [Test]
    public async Task SelectOldestOwned_SingleRow_IsThatRowWithNoResidue()
    {
        var selected = BotPresenceCoordinator.SelectOldestOwned([(4242u, "Citizen1")]);

        await Assert.That(selected).IsNotNull();
        await Assert.That(selected!.Value.Name).IsEqualTo("Citizen1");
        await Assert.That(selected.Value.Residue).IsEqualTo(0);
    }

    [Test]
    public async Task SelectOldestOwned_MultipleRows_PicksTheOldest_AndCountsTheNewerResidue()
    {
        // Rows arrive newest-first from the loader in practice; the pick must
        // not depend on that order.
        var selected = BotPresenceCoordinator.SelectOldestOwned([
            (900u, "Citizen3"),
            (700u, "Citizen2"),
            (500u, "Citizen1"),
        ]);

        await Assert.That(selected).IsNotNull();
        await Assert.That(selected!.Value.Name).IsEqualTo("Citizen1");
        await Assert.That(selected.Value.Residue).IsEqualTo(2);
    }
}
