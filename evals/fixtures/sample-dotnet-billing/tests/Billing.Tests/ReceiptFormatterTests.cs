using Billing;
using Xunit;

namespace Billing.Tests;

public sealed class ReceiptFormatterTests
{
    private const int Width = ReceiptFormatter.DescriptionWidth;

    [Fact]
    public void Render_WritesOneLinePerEntryAndATotal()
    {
        ReceiptLine[] lines =
        [
            new("Widget", 9.99m),
            new("Shipping", 4.01m),
        ];

        var expected =
            "Widget".PadRight(Width) + " GBP 9.99\n" +
            "Shipping".PadRight(Width) + " GBP 4.01\n" +
            "TOTAL".PadRight(Width) + " GBP 14.00\n";

        Assert.Equal(expected, ReceiptFormatter.Render(lines, "GBP"));
    }

    [Fact]
    public void Render_WritesAZeroTotal_ForNoLines()
    {
        Assert.Equal(
            "TOTAL".PadRight(Width) + " USD 0.00\n",
            ReceiptFormatter.Render([], "USD"));
    }

    [Fact]
    public void Render_TruncatesAnOverlongDescription()
    {
        var long_ = new string('x', Width + 10);

        var rendered = ReceiptFormatter.Render([new ReceiptLine(long_, 1m)], "GBP");

        Assert.StartsWith(new string('x', Width) + " GBP 1.00\n", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Render_RejectsAMalformedCurrencyCode()
    {
        Assert.Throws<ArgumentException>(() => ReceiptFormatter.Render([], ""));
    }
}
