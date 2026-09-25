using Microsoft.EntityFrameworkCore;
using OrderProcessing.Domain.Common;
using OrderProcessing.Domain.Customers;
using OrderProcessing.Domain.Products;

namespace OrderProcessing.Infrastructure.Persistence;

/// <summary>
/// Development seed data, so a reviewer can exercise the API immediately after
/// <c>dotnet run</c> without creating products or customers first
/// (specification section 13.1).
/// </summary>
/// <remarks>
/// Identifiers are fixed rather than random so they can be quoted in the README and
/// the sample request file, and so repeated runs are reproducible.
/// </remarks>
public static class DatabaseSeeder
{
    public static readonly Guid AliceId = new("11111111-1111-1111-1111-111111111111");
    public static readonly Guid BobId = new("22222222-2222-2222-2222-222222222222");
    public static readonly Guid AdminId = new("33333333-3333-3333-3333-333333333333");

    public static readonly Guid KeyboardId = new("aaaaaaaa-0000-0000-0000-000000000001");
    public static readonly Guid MouseId = new("aaaaaaaa-0000-0000-0000-000000000002");
    public static readonly Guid MonitorId = new("aaaaaaaa-0000-0000-0000-000000000003");
    public static readonly Guid HeadsetId = new("aaaaaaaa-0000-0000-0000-000000000004");
    public static readonly Guid DiscontinuedId = new("aaaaaaaa-0000-0000-0000-000000000005");

    private const string Currency = "USD";

    public static async Task SeedAsync(
        OrderProcessingDbContext dbContext,
        TimeProvider timeProvider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(dbContext);
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (await dbContext.Customers.AnyAsync(cancellationToken))
        {
            return;
        }

        var now = timeProvider.GetUtcNow();

        dbContext.Customers.AddRange(
            Customer.Create(AliceId, "alice@example.com", "Alice Anderson", now),
            Customer.Create(BobId, "bob@example.com", "Bob Brown", now),
            Customer.Create(AdminId, "admin@example.com", "Admin User", now));

        dbContext.Products.AddRange(
            Product.Create(KeyboardId, "KB-001", "Mechanical Keyboard", Money.FromDecimal(49.99m, Currency)),
            Product.Create(MouseId, "MS-001", "Wireless Mouse", Money.FromDecimal(19.99m, Currency)),
            Product.Create(MonitorId, "MN-001", "27-inch Monitor", Money.FromDecimal(299.00m, Currency)),
            Product.Create(HeadsetId, "HS-001", "Noise-cancelling Headset", Money.FromDecimal(129.50m, Currency)),

            // Present so the "inactive product is rejected" path (FR-1.3) can be
            // exercised by hand, not only in tests.
            Product.Create(
                DiscontinuedId,
                "DC-001",
                "Discontinued Webcam",
                Money.FromDecimal(59.99m, Currency),
                isActive: false));

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
