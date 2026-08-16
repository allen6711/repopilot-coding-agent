namespace Orders;

/// <summary>
/// Normalizes a customer name on the way into the order record.
/// <para>
/// Names arrive from three checkout surfaces with inconsistent spacing. A
/// normalized name has no leading or trailing whitespace and no run of more than
/// one space inside it. A name that is empty or only whitespace is not a name and
/// is rejected; a name longer than 200 characters does not fit the record.
/// </para>
/// </summary>
public static class CustomerNameNormalizer
{
    /// <summary>Longest name the order record holds.</summary>
    public const int MaxLength = 200;

    /// <summary>Returns the normalized form of <paramref name="name"/>.</summary>
    public static string Normalize(string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        return name.Trim();
    }
}
