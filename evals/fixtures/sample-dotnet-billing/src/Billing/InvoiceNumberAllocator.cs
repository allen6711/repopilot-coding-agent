namespace Billing;

/// <summary>
/// Hands out invoice numbers.
/// <para>
/// Numbers are sequential within a calendar year and restart at 1 when the year
/// changes, so the first invoice of any year is <c>INV-{year}-00001</c>. No
/// number is ever handed out twice.
/// </para>
/// </summary>
public sealed class InvoiceNumberAllocator
{
    private int _year;
    private int _last;

    /// <summary>Allocates the next number for <paramref name="year"/>.</summary>
    public string Next(int year)
    {
        if (year != _year)
        {
            _year = year;
            _last = 0;
        }

        return $"INV-{_year}-{_last++:D5}";
    }
}
