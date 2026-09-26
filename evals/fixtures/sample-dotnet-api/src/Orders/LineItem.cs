namespace Orders;

/// <summary>One line of an order.</summary>
/// <param name="Sku">Stock keeping unit. Required.</param>
/// <param name="Quantity">How many units. Must be between 1 and 999 inclusive.</param>
/// <param name="UnitPrice">Price of a single unit.</param>
public sealed record LineItem(string Sku, int Quantity, decimal UnitPrice);
