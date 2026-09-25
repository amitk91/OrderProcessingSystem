using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Infrastructure.Persistence;

namespace OrderProcessing.Infrastructure.Orders;

/// <summary>
/// Generates sequential, human-readable order numbers such as <c>ORD-2026-000042</c>
/// (FR-1.10).
/// </summary>
/// <remarks>
/// The sequence is derived from the highest existing number for the current year.
/// This is adequate for a single-writer SQLite deployment and is backed by a unique
/// index, so a collision fails loudly rather than producing duplicates. A
/// multi-writer deployment would use a database sequence instead — noted in the
/// README as a known limitation rather than left as a silent assumption.
/// </remarks>
internal sealed class OrderNumberGenerator(
    OrderProcessingDbContext dbContext,
    TimeProvider timeProvider) : IOrderNumberGenerator
{
    private const string Prefix = "ORD";

    public async Task<string> NextAsync(CancellationToken cancellationToken = default)
    {
        var year = timeProvider.GetUtcNow().Year;
        var yearPrefix = $"{Prefix}-{year}-";

        var lastNumber = await dbContext.Orders
            .AsNoTracking()
            .Where(order => order.OrderNumber.StartsWith(yearPrefix))
            .OrderByDescending(order => order.OrderNumber)
            .Select(order => order.OrderNumber)
            .FirstOrDefaultAsync(cancellationToken);

        var next = 1;
        if (lastNumber is not null
            && int.TryParse(
                lastNumber[yearPrefix.Length..],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var lastSequence))
        {
            next = lastSequence + 1;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{yearPrefix}{next:D6}");
    }
}
