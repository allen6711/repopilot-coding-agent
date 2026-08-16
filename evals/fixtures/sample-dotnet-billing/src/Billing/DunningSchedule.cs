namespace Billing;

/// <summary>What collections does about an overdue invoice.</summary>
public enum DunningAction
{
    /// <summary>Nothing yet — the invoice is not overdue.</summary>
    None,

    /// <summary>A courtesy reminder.</summary>
    Reminder,

    /// <summary>A formal notice; the account is flagged.</summary>
    FinalNotice,

    /// <summary>The account is suspended and the debt handed on.</summary>
    Suspend,
}

/// <summary>
/// Decides what collections does about an invoice, given how overdue it is.
/// <para>
/// Nothing happens until an invoice is overdue. From day 1 it gets a reminder,
/// from day 15 a final notice, and from day 30 the account is suspended. The
/// boundaries are inclusive: day 15 is a final notice, not a reminder.
/// </para>
/// </summary>
public static class DunningSchedule
{
    /// <summary>Days overdue at which a final notice is sent.</summary>
    public const int FinalNoticeDay = 15;

    /// <summary>Days overdue at which the account is suspended.</summary>
    public const int SuspendDay = 30;

    /// <summary>The action due for an invoice <paramref name="daysOverdue"/> days late.</summary>
    public static DunningAction ActionFor(int daysOverdue)
    {
        if (daysOverdue < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(daysOverdue), daysOverdue, "Days overdue is not negative.");
        }

        if (daysOverdue >= SuspendDay)
        {
            return DunningAction.Suspend;
        }

        if (daysOverdue >= FinalNoticeDay)
        {
            return DunningAction.FinalNotice;
        }

        if (daysOverdue >= 1)
        {
            return DunningAction.Reminder;
        }

        return DunningAction.None;
    }
}
