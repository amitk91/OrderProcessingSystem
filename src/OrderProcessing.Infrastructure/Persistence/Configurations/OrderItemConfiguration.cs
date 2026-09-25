using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Infrastructure.Persistence.Configurations;

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("order_items", table =>
        {
            // Enforced in the database as well as the domain: a defect that bypasses
            // the aggregate should still be rejected at the last line of defence.
            table.HasCheckConstraint("CK_OrderItems_Quantity_Positive", "\"Quantity\" > 0");
            table.HasCheckConstraint("CK_OrderItems_UnitPrice_NonNegative", "\"UnitPriceMinor\" >= 0");
        });

        builder.HasKey(item => item.Id);

        builder.Property(item => item.Id)
            .HasConversion(SqliteValueConverters.Guid)
            .ValueGeneratedNever();

        builder.Property(item => item.OrderId)
            .HasConversion(SqliteValueConverters.Guid)
            .IsRequired();

        builder.Property(item => item.ProductId)
            .HasConversion(SqliteValueConverters.Guid)
            .IsRequired();

        // Snapshot, not a join to the live catalogue (specification section 5.2).
        builder.Property(item => item.ProductName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(item => item.UnitPriceMinor).IsRequired();
        builder.Property(item => item.LineTotalMinor).IsRequired();
        builder.Property(item => item.Quantity).IsRequired();

        builder.Property(item => item.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder.HasIndex(item => item.OrderId)
            .HasDatabaseName("IX_OrderItems_OrderId");
    }
}
