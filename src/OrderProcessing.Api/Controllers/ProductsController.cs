using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OrderProcessing.Application.Abstractions;

namespace OrderProcessing.Api.Controllers;

/// <summary>A catalogue product, as returned to clients.</summary>
public sealed record ProductDto(Guid Id, string Sku, string Name, decimal UnitPrice, string Currency, bool IsActive);

/// <summary>
/// Read-only catalogue access, so a client can discover product ids before ordering.
/// </summary>
/// <remarks>
/// Depends on the <see cref="IProductCatalog"/> port rather than a <c>DbContext</c>, so
/// the API project holds no persistence knowledge outside its composition root.
/// </remarks>
[ApiController]
[Route("api/v1/products")]
[Authorize]
[Produces("application/json")]
public sealed class ProductsController(IProductCatalog catalog) : ControllerBase
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
        var products = await catalog.ListAsync(includeInactive, cancellationToken);

        return Ok(products
            .Select(product => new ProductDto(
                product.Id,
                product.Sku,
                product.Name,
                product.UnitPrice.Amount,
                product.Currency,
                product.IsActive))
            .ToList());
    }
}
