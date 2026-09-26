using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class DunningScheduleTests
{
    [Fact]
    public void ActionFor_DoesNothing_WhileTheInvoiceIsNotOverdue()
    {
        Assert.Equal(DunningAction.None, DunningSchedule.ActionFor(0));
    }

    [Fact]
    public void ActionFor_SendsAReminder_OnTheFirstOverdueDay()
    {
        Assert.Equal(DunningAction.Reminder, DunningSchedule.ActionFor(1));
    }
}
