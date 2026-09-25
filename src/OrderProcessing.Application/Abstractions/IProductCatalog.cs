using OrderProcessing.Domain.Products;

namespace OrderProcessing.Application.Abstractions;

/// <summary>
/// Read access to the product catalogue.
/// </summary>
/// <remarks>
/// Separate from <see cref="IOrderRepository"/> rather than folded into it: order
/// placement needs to price items, but has no business modifying the catalogue, and
/// catalogue browsing has nothing to do with orders. Two narrow ports beat one wide one.
/// </remarks>
public interface IProductCatalog
{
    /// <summary>
    /// Loads the requested products, keyed by id. Missing ids are simply absent from
    /// the result, so the caller decides how to report them.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, Product>> GetByIdsAsync(
        IReadOnlyCollection<Guid> productIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<Product>> ListAsync(
        bool includeInactive,
        CancellationToken cancellationToken = default);
}
