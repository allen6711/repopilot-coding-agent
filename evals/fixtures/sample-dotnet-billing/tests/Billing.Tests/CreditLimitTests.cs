using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class CreditLimitTests
{
    [Fact]
    public void Allows_AChargeWellInsideTheLimit()
    {
        Assert.True(CreditLimit.Allows(currentBalance: 100m, charge: 50m, limit: 500m));
    }

    [Fact]
    public void Refuses_AChargeThatWouldExceedTheLimit()
    {
        Assert.False(CreditLimit.Allows(currentBalance: 480m, charge: 50m, limit: 500m));
    }
}
