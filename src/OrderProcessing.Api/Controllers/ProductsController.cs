using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderProcessing.Infrastructure.Persistence;

namespace OrderProcessing.Api.Controllers;

/// <summary>A catalogue product, as returned to clients.</summary>
public sealed record ProductDto(Guid Id, string Sku, string Name, decimal UnitPrice, string Currency, bool IsActive);

/// <summary>
/// Read-only catalogue access, so a client can discover product ids before ordering.
/// </summary>
[ApiController]
[Route("api/v1/products")]
[Authorize]
[Produces("application/json")]
public sealed class ProductsController(OrderProcessingDbContext dbContext) : ControllerBase
{
    /// <summary>Lists catalogue products.</summary>
    /// <param name="includeInactive">
    /// Include products that can no longer be ordered. Useful for demonstrating the
    /// inactive-product rejection path (FR-1.3).
    /// </param>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<ProductDto>), StatusCodes.Status200OK)]
    public async Task<ActionResult<IReadOnlyList<ProductDto>>> ListAsync(
        [FromQuery] bool includeInactive = false,
        CancellationToken cancellationToken = default)
    {
        var query = dbContext.Products.AsNoTracking();

        if (!includeInactive)
        {
            query = query.Where(product => product.IsActive);
        }

        var products = await query
            .OrderBy(product => product.Name)
            .Select(product => new ProductDto(
                product.Id,
                product.Sku,
                product.Name,
                product.UnitPriceMinor / 100m,
                product.Currency,
                product.IsActive))
            .ToListAsync(cancellationToken);

        return Ok(products);
    }
}
