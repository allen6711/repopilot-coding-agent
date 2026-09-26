namespace Billing;

/// <summary>One line of a receipt.</summary>
/// <param name="Description">What was billed.</param>
/// <param name="Amount">What it cost.</param>
public sealed record ReceiptLine(string Description, decimal Amount);

/// <summary>
/// Renders a plain-text receipt.
/// </summary>
public static class ReceiptFormatter
{
    /// <summary>Width the description column is padded to.</summary>
    public const int DescriptionWidth = 30;

    /// <summary>
    /// Renders <paramref name="lines"/> with a total, one line per entry.
    /// </summary>
    public static string Render(IReadOnlyList<ReceiptLine> lines, string currencyCode)
    {
        ArgumentNullException.ThrowIfNull(lines);
        CurrencyCodeValidator.Validate(currencyCode);

        var text = "";
        var total = 0m;

        for (var i = 0; i < lines.Count; i++)
        {
            var description = lines[i].Description;

            if (description.Length > DescriptionWidth)
            {
                description = description.Substring(0, DescriptionWidth);
            }

            while (description.Length < DescriptionWidth)
            {
                description = description + " ";
            }

            text = text + description + " " + currencyCode + " "
                + lines[i].Amount.ToString("0.00") + "\n";

            total = total + lines[i].Amount;
        }

        var totalLabel = "TOTAL";

        while (totalLabel.Length < DescriptionWidth)
        {
            totalLabel = totalLabel + " ";
        }

        text = text + totalLabel + " " + currencyCode + " " + total.ToString("0.00") + "\n";

        return text;
    }
}
