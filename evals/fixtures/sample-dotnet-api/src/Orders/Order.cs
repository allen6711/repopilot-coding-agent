namespace Orders;

public sealed record Address(string City, string PostalCode);

public sealed record Order(
    string Id,
    string CustomerName,
    Address? ShippingAddress,
    decimal TotalAmount);

public sealed record OrderView(
    string Id,
    string CustomerName,
    string? ShippingCity,
    string? ShippingPostalCode,
    decimal TotalAmount);

public interface IOrderRepository
{
    Order? Find(string orderId);
}
