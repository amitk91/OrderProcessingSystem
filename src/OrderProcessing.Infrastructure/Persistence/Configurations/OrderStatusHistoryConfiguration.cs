using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderProcessing.Domain.Orders;

namespace OrderProcessing.Infrastructure.Persistence.Configurations;

internal sealed class OrderStatusHistoryConfiguration : IEntityTypeConfiguration<OrderStatusHistory>
{
    public void Configure(EntityTypeBuilder<OrderStatusHistory> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("order_status_history");

        builder.HasKey(history => history.Id);

        builder.Property(history => history.Id)
            .HasConversion(SqliteValueConverters.Guid)
            .ValueGeneratedNever();

        builder.Property(history => history.OrderId)
            .HasConversion(SqliteValueConverters.Guid)
            .IsRequired();

        // Null for the entry written when the order is created.
        builder.Property(history => history.FromStatus)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Property(history => history.ToStatus)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        builder.Property(history => history.ChangedBy)
            .HasConversion<string>()
            .HasMaxLength(16)
            .IsRequired();

        // Null for transitions made by the background job.
        builder.Property(history => history.ChangedByUserId)
            .HasConversion(SqliteValueConverters.NullableGuid);

        builder.Property(history => history.Reason)
            .HasMaxLength(500);

        builder.Property(history => history.ChangedAt)
            .HasConversion(SqliteValueConverters.DateTimeOffset)
            .IsRequired();

        builder.HasIndex(history => new { history.OrderId, history.ChangedAt })
            .HasDatabaseName("IX_OrderStatusHistory_OrderId_ChangedAt");
    }
}
