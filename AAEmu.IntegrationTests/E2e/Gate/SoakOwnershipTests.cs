using AAEmu.IntegrationTests.E2e;
using Xunit;

namespace AAEmu.IntegrationTests.E2e.Gate;

public sealed class SoakOwnershipTests
{
    [Fact]
    public void FindNewOwnedRows_ExcludesPreexistingSiblingRows()
    {
        var before = new[]
        {
            new E2eStack.OwnedBotRow(10, 100, false),
            new E2eStack.OwnedBotRow(20, 200, false)
        };
        var after = new[]
        {
            before[0],
            before[1],
            new E2eStack.OwnedBotRow(30, 300, false)
        };

        var owned = E2eStack.FindNewOwnedRows(before, after);

        var row = Assert.Single(owned);
        Assert.Equal((uint)30, row.AccountId);
        Assert.Equal((uint)300, row.CharacterId);
        Assert.True(row.AccountCreated);
    }

    [Fact]
    public void FindNewOwnedRows_PreservesPreexistingAccountOwnership()
    {
        var before = new[]
        {
            new E2eStack.OwnedBotRow(10, 100, false)
        };
        var after = new[]
        {
            before[0],
            new E2eStack.OwnedBotRow(10, 101, false)
        };

        var owned = E2eStack.FindNewOwnedRows(before, after);

        var row = Assert.Single(owned);
        Assert.Equal((uint)10, row.AccountId);
        Assert.Equal((uint)101, row.CharacterId);
        Assert.False(row.AccountCreated);
    }
}
