using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Domain.Orders;
using OrderProcessing.Infrastructure.Persistence;

namespace OrderProcessing.Infrastructure.Scheduling;

/// <summary>
/// SQLite implementation of <see cref="IPendingOrderClaimer"/>
/// (specification section 9.3).
/// </summary>
/// <remarks>
/// SQLite serialises writes behind a database-level lock, so a single statement is
/// inherently atomic. The claim is therefore expressed as one conditional
/// <c>UPDATE ... RETURNING</c> rather than the <c>SELECT ... FOR UPDATE SKIP LOCKED</c>
/// used under PostgreSQL, which SQLite does not support.
///
/// Two details carry the correctness argument:
/// <list type="number">
///   <item>The outer <c>WHERE ... AND status = 'Pending'</c> re-asserts the expected
///   state at execution time, so an order cancelled between the inner select and the
///   update is not promoted (FR-6.6).</item>
///   <item><c>RETURNING</c> reports exactly which rows changed, so history is written
///   only for orders genuinely transitioned — never for ones another worker took.</item>
/// </list>
///
/// The version column is incremented in the same statement, keeping the optimistic
/// concurrency token consistent with writes made through the aggregate.
/// </remarks>
internal sealed class SqlitePendingOrderClaimer(
    OrderProcessingDbContext dbContext,
    ILogger<SqlitePendingOrderClaimer> logger) : IPendingOrderClaimer
{
    private const string ClaimSql = """
        UPDATE orders
        SET "Status" = $newStatus,
            "UpdatedAt" = $now,
            "Version" = "Version" + 1
        WHERE "Id" IN (
            SELECT "Id" FROM orders
            WHERE "Status" = $pendingStatus
            ORDER BY "CreatedAt"
            LIMIT $batchSize
        )
        AND "Status" = $pendingStatus
        RETURNING "Id";
        """;

    public async Task<IReadOnlyList<Guid>> ClaimPendingOrdersAsync(
        int batchSize,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var claimedIds = new List<Guid>(batchSize);
        var connection = (SqliteConnection)dbContext.Database.GetDbConnection();

        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed)
        {
            await connection.OpenAsync(cancellationToken);
        }

        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = ClaimSql;

            // Enlist in the ambient transaction when the caller has opened one, so the
            // claim and any follow-up writes commit or roll back together.
            if (dbContext.Database.CurrentTransaction?.GetDbTransaction() is SqliteTransaction transaction)
            {
                command.Transaction = transaction;
            }

            command.Parameters.AddWithValue("$newStatus", nameof(OrderStatus.Processing));
            command.Parameters.AddWithValue("$pendingStatus", nameof(OrderStatus.Pending));
            command.Parameters.AddWithValue("$batchSize", batchSize);
            command.Parameters.AddWithValue(
                "$now",
                occurredAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                claimedIds.Add(Guid.Parse(reader.GetString(0)));
            }
        }
        finally
        {
            if (wasClosed)
            {
                await connection.CloseAsync();
            }
        }

        if (claimedIds.Count > 0)
        {
            PromotionLog.OrdersClaimed(logger, claimedIds.Count);
        }

        return claimedIds;
    }
}
