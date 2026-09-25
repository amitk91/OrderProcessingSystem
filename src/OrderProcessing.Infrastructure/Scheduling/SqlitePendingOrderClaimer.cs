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
/// <para>SQLite has no <c>FOR UPDATE SKIP LOCKED</c>, so the lock is taken by writing:
/// a single atomic statement stamps a lease onto pending rows and reports which ones it
/// stamped. Because SQLite serialises writes, that statement cannot interleave with
/// another worker's, so two workers never stamp the same row.</para>
///
/// <para><b>The lease is a lock, not domain state.</b> It is an EF Core <em>shadow
/// property</em> — present in the database and the persistence model but not on the
/// <see cref="Order"/> aggregate — so a scheduling mechanism cannot leak into the
/// domain. It is cleared in the same transaction that promotes the order, so it never
/// outlives the work it guards and there are no stale leases to reap.</para>
///
/// <para>The status is deliberately <em>not</em> changed here. Promotion goes through
/// <c>Order.PromoteToProcessing</c> so the transition matrix remains the single point
/// of enforcement, and the status change commits together with its audit entry.</para>
/// </remarks>
internal sealed class SqlitePendingOrderClaimer(
    OrderProcessingDbContext dbContext,
    ILogger<SqlitePendingOrderClaimer> logger) : IPendingOrderClaimer
{
    private const string ClaimSql = """
        UPDATE orders
        SET "PromotionLease" = $lease
        WHERE "Id" IN (
            SELECT "Id" FROM orders
            WHERE "Status" = $pendingStatus
              AND "PromotionLease" IS NULL
            ORDER BY "CreatedAt"
            LIMIT $batchSize
        )
        AND "Status" = $pendingStatus
        AND "PromotionLease" IS NULL
        RETURNING "Id";
        """;

    public async Task<IReadOnlyList<Guid>> ClaimPendingOrdersAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);

        var transaction = dbContext.Database.CurrentTransaction
            ?? throw new InvalidOperationException(
                "A claim must be made inside a transaction so it is released if the run is abandoned. " +
                $"Open one via {nameof(IOrderRepository)}.{nameof(IOrderRepository.BeginTransactionAsync)}.");

        var connection = (SqliteConnection)dbContext.Database.GetDbConnection();
        var claimedIds = new List<Guid>(batchSize);

        await using var command = connection.CreateCommand();
        command.CommandText = ClaimSql;
        command.Transaction = (SqliteTransaction)transaction.GetDbTransaction();

        command.Parameters.AddWithValue("$pendingStatus", nameof(OrderStatus.Pending));
        command.Parameters.AddWithValue("$batchSize", batchSize);
        command.Parameters.AddWithValue(
            "$lease",
            Guid.CreateVersion7().ToString("D", CultureInfo.InvariantCulture));

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                claimedIds.Add(Guid.Parse(reader.GetString(0)));
            }
        }

        if (claimedIds.Count > 0)
        {
            PromotionLog.OrdersClaimed(logger, claimedIds.Count);
        }

        return claimedIds;
    }
}
