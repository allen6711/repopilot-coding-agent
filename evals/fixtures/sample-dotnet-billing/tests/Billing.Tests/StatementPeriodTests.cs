using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class StatementPeriodTests
{
    private static readonly StatementPeriod June =
        new(new DateOnly(2026, 6, 1), new DateOnly(2026, 6, 30));

    [Fact]
    public void Contains_TheFirstDay()
    {
        Assert.True(June.Contains(new DateOnly(2026, 6, 1)));
    }

    [Fact]
    public void Contains_ADayInTheMiddle()
    {
        Assert.True(June.Contains(new DateOnly(2026, 6, 15)));
    }

    [Fact]
    public void Contains_TheLastDay()
    {
        Assert.True(June.Contains(new DateOnly(2026, 6, 30)));
    }

    [Fact]
    public void DoesNotContain_TheDayAfterItEnds()
    {
        Assert.False(June.Contains(new DateOnly(2026, 7, 1)));
    }

    [Fact]
    public void DayCount_CountsBothEnds()
    {
        Assert.Equal(30, June.DayCount);
    }
}
