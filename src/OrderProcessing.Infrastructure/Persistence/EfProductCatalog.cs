using Microsoft.EntityFrameworkCore;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Domain.Products;

namespace OrderProcessing.Infrastructure.Persistence;

/// <summary>
/// EF Core implementation of <see cref="IProductCatalog"/>.
/// </summary>
internal sealed class EfProductCatalog(OrderProcessingDbContext dbContext) : IProductCatalog
{
    public async Task<IReadOnlyDictionary<Guid, Product>> GetByIdsAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(productIds);

        if (productIds.Count == 0)
        {
            return new Dictionary<Guid, Product>();
        }

        return await dbContext.Products
            .AsNoTracking()
            .Where(product => productIds.Contains(product.Id))
            .ToDictionaryAsync(product => product.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<Product>> ListAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Products.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(product => product.IsActive);
        }

        return await query
            .OrderBy(product => product.Name)
            .ToListAsync(cancellationToken);
    }
}
