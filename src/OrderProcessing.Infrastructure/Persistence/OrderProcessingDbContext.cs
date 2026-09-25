using Microsoft.EntityFrameworkCore;
using OrderProcessing.Domain.Customers;
using OrderProcessing.Domain.Orders;
using OrderProcessing.Domain.Products;

namespace OrderProcessing.Infrastructure.Persistence;

/// <summary>
/// EF Core context for the orders bounded context.
/// </summary>
/// <remarks>
/// Entity configuration lives in <c>Persistence/Configurations</c> rather than here,
/// so this type stays a composition point. Value conversions for GUIDs, timestamps and
/// money are applied there too — SQLite has no native type for any of them
/// (specification section 5.1).
/// </remarks>
public sealed class OrderProcessingDbContext(DbContextOptions<OrderProcessingDbContext> options)
    : DbContext(options)
{
    public DbSet<Order> Orders => Set<Order>();

    public DbSet<OrderItem> OrderItems => Set<OrderItem>();

    public DbSet<OrderStatusHistory> OrderStatusHistory => Set<OrderStatusHistory>();

    public DbSet<Customer> Customers => Set<Customer>();

    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrderProcessingDbContext).Assembly);

        base.OnModelCreating(modelBuilder);
    }
}
