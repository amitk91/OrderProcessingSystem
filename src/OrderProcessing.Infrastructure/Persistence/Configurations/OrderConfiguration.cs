using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("orders");

        builder.HasKey(order => order.Id);

        builder.Property(order => order.Id)
            .HasConversion(SqliteValueConverters.Guid)
            .ValueGeneratedNever();

        builder.Property(order => order.OrderNumber)
            .HasMaxLength(20)
            .IsRequired();

        builder.Property(order => order.CustomerId)
            .HasConversion(SqliteValueConverters.Guid)
            .IsRequired();

        // Stored as text so the database stays readable and is not coupled to enum
        // ordinals, which would silently reinterpret data if the enum were reordered.
        builder.Property(order => order.Status)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(order => order.TotalAmountMinor)
            .IsRequired();

        builder.Property(order => order.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(order => order.IdempotencyKey)
            .HasMaxLength(64);

        builder.Property(order => order.CreatedAt)
            .HasConversion(SqliteValueConverters.DateTimeOffset)
            .IsRequired();

        builder.Property(order => order.UpdatedAt)
            .HasConversion(SqliteValueConverters.DateTimeOffset)
            .IsRequired();

        builder.Property(order => order.CancelledAt)
            .HasConversion(SqliteValueConverters.NullableDateTimeOffset);

        builder.Property(order => order.CancelledBy)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(order => order.CancellationReason)
            .HasMaxLength(500);

        // Optimistic concurrency (specification section 10.2). SQLite has no
        // server-generated row version, so the domain increments this and EF Core
        // appends "WHERE version = @original" to every update. A concurrent write
        // therefore fails loudly instead of silently overwriting.
        builder.Property(order => order.Version)
            .IsConcurrencyToken()
            .IsRequired();

        // Shadow property: the promotion lease exists in the database and in this
        // model, but not on the Order aggregate. Claiming is a scheduling concern
        // (specification section 9.3), and keeping it out of the domain means the
        // aggregate has no idea it can be queued.
        builder.Property<string?>("PromotionLease")
            .HasMaxLength(64);

        builder.HasMany(order => order.Items)
            .WithOne()
            .HasForeignKey(item => item.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasMany(order => order.StatusHistory)
            .WithOne()
            .HasForeignKey(history => history.OrderId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Navigation(order => order.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.Navigation(order => order.StatusHistory).UsePropertyAccessMode(PropertyAccessMode.Field);

        // Section 5.3. The dominant read is "this customer's orders, newest first".
        builder.HasIndex(order => new { order.CustomerId, order.CreatedAt })
            .HasDatabaseName("IX_Orders_CustomerId_CreatedAt");

        // Serves both admin status filtering and the scheduler's claim query.
        builder.HasIndex(order => new { order.Status, order.CreatedAt })
            .HasDatabaseName("IX_Orders_Status_CreatedAt");

        // The claim query filters on an unleased pending row, so the lease belongs in
        // the index that serves it.
        builder.HasIndex(order => order.Status)
            .HasFilter("\"PromotionLease\" IS NULL")
            .HasDatabaseName("IX_Orders_Status_Unleased");

        // Supports sorting by total (FR-4.7) without a scan.
        builder.HasIndex(order => new { order.CustomerId, order.TotalAmountMinor })
            .HasDatabaseName("IX_Orders_CustomerId_TotalAmountMinor");

        builder.HasIndex(order => order.OrderNumber)
            .IsUnique()
            .HasDatabaseName("UX_Orders_OrderNumber");

        // Filtered so the many orders without a key do not collide on NULL.
        // Scoped per customer: one customer's key must not block another's.
        builder.HasIndex(order => new { order.CustomerId, order.IdempotencyKey })
            .IsUnique()
            .HasFilter("\"IdempotencyKey\" IS NOT NULL")
            .HasDatabaseName("UX_Orders_CustomerId_IdempotencyKey");
    }
}
