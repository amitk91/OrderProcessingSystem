namespace OrderProcessing.Domain.Orders;

/// <summary>
/// Lifecycle states of an order, per specification section 6.1.
/// Persisted as a string so the database stays readable and is not coupled
/// to the ordinal values of this enum.
/// </summary>
public enum OrderStatus
{
    Pending,
    Processing,
    Shipped,
    Delivered,
    Cancelled
}
