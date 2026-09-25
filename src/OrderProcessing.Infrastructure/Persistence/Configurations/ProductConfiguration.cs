using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using OrderProcessing.Domain.Products;

namespace OrderProcessing.Infrastructure.Persistence.Configurations;

internal sealed class ProductConfiguration : IEntityTypeConfiguration<Product>
{
    public void Configure(EntityTypeBuilder<Product> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.ToTable("products", table =>
            table.HasCheckConstraint("CK_Products_UnitPrice_NonNegative", "\"UnitPriceMinor\" >= 0"));

        builder.HasKey(product => product.Id);

        builder.Property(product => product.Id)
            .HasConversion(SqliteValueConverters.Guid)
            .ValueGeneratedNever();

        builder.Property(product => product.Sku)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(product => product.Name)
            .HasMaxLength(200)
            .IsRequired();

        builder.Property(product => product.UnitPriceMinor).IsRequired();

        builder.Property(product => product.Currency)
            .HasMaxLength(3)
            .IsRequired();

        builder.Property(product => product.IsActive).IsRequired();

        builder.HasIndex(product => product.Sku)
            .IsUnique()
            .HasDatabaseName("UX_Products_Sku");
    }
}
