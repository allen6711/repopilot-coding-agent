namespace Orders;

/// <summary>A resolved page request.</summary>
/// <param name="Number">1-based page number.</param>
/// <param name="Size">How many rows the page carries.</param>
public sealed record PageRequest(int Number, int Size);

/// <summary>
/// Turns the page parameters a caller sent into the page the orders list will
/// actually serve.
/// </summary>
public static class OrderPaging
{
    /// <summary>Page size used when the caller does not ask for one.</summary>
    public const int DefaultSize = 25;

    /// <summary>Largest page the orders list will serve.</summary>
    public const int MaxSize = 100;

    /// <summary>
    /// Resolves a requested page number and size.
    /// </summary>
    /// <param name="number">Requested page; anything below 1 becomes 1.</param>
    /// <param name="size">Requested size; null or below 1 becomes the default.</param>
    public static PageRequest Resolve(int? number, int? size)
    {
        var resolvedNumber = number is null or < 1 ? 1 : number.Value;
        var resolvedSize = size is null or < 1 ? DefaultSize : size.Value;

        return new PageRequest(resolvedNumber, resolvedSize);
    }
}
