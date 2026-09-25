using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderProcessing.Domain.Customers;

namespace OrderProcessing.Infrastructure.Persistence.Configurations;

internal sealed class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("customers");

        builder.HasKey(customer => customer.Id);

        builder.Property(customer => customer.Id)
            .HasConversion(SqliteValueConverters.Guid)
            .ValueGeneratedNever();

        builder.Property(customer => customer.Email)
            .HasMaxLength(256)
            .UseCollation("NOCASE")
            .IsRequired();

        builder.Property(customer => customer.FullName)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(customer => customer.CreatedAt)
            .HasConversion(SqliteValueConverters.DateTimeOffset)
            .IsRequired();

        builder.HasIndex(customer => customer.Email)
            .IsUnique()
            .HasDatabaseName("UX_Customers_Email");
    }
}
