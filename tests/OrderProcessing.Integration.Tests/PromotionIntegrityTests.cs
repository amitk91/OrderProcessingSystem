using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Domain.Orders;
using OrderProcessing.Infrastructure.Persistence;
using OrderProcessing.Integration.Tests.Infrastructure;
using Shouldly;

namespace OrderProcessing.Integration.Tests;

/// <summary>
/// Guards the two properties that the promotion path is easiest to get wrong:
/// the transition must go through the aggregate, and the status change must be
/// atomic with its audit entry.
/// </summary>
/// <remarks>
/// Added after a review observed that the claim statement was transitioning orders in
/// SQL while the documentation claimed every transition routed through
/// <see cref="Order.TransitionTo"/>. The behaviour happened to be correct, but the rule
/// was duplicated and — more seriously — the two writes were not in one transaction.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class PromotionIntegrityTests(ApiFactory factory)
{
    [Fact]
    public async Task Claiming_alone_does_not_change_an_orders_status()
    {
        // The claim acquires the exclusive right to work on an order. It is a
        // concurrency concern, not a business transition — so on its own it must
        // leave the order exactly as it found it.
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        await using (var scope = factory.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
            var claimer = scope.ServiceProvider.GetRequiredService<IPendingOrderClaimer>();

            await using var transaction = await repository.BeginTransactionAsync();
            var claimed = await claimer.ClaimPendingOrdersAsync(100);

            claimed.ShouldContain(order.Id);

            // Deliberately abandoned: no commit, simulating a worker that dies after
            // claiming. Nothing may persist.
        }

        var reread = await (await alice.GetAsync(
            new Uri($"/api/v1/orders/{order.Id}", UriKind.Relative))).ReadOrderAsync();

        reread.Status.ShouldBe("PENDING", "an abandoned claim must leave the order untouched");
        reread.StatusHistory.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Claiming_only_ever_returns_orders_that_are_pending()
    {
        // The claim's contract, asserted directly. Without this, the only thing
        // stopping a non-pending order being claimed is the aggregate rejecting the
        // transition afterwards — which is correct behaviour but records a failure
        // rather than never selecting the row in the first place.
        var alice = factory.CreateAliceClient();
        var admin = factory.CreateAdminClient();

        var pending = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        var cancelled = await (await alice.CreateOrderAsync(DatabaseSeeder.MouseId)).ReadOrderAsync();
        await alice.CancelOrderAsync(cancelled.Id);

        var processing = await (await alice.CreateOrderAsync(DatabaseSeeder.MonitorId)).ReadOrderAsync();
        await admin.UpdateStatusAsync(processing.Id, "PROCESSING");

        await using var scope = factory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IOrderRepository>();
        var claimer = scope.ServiceProvider.GetRequiredService<IPendingOrderClaimer>();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();

        await using var transaction = await repository.BeginTransactionAsync();
        var claimedIds = await claimer.ClaimPendingOrdersAsync(1000);

        claimedIds.ShouldContain(pending.Id);
        claimedIds.ShouldNotContain(cancelled.Id, "a cancelled order must never be claimed");
        claimedIds.ShouldNotContain(processing.Id, "an order already processing must never be claimed");

        // Nothing outside Pending, whatever else the shared database holds.
        var nonPending = await dbContext.Orders
            .AsNoTracking()
            .Where(order => claimedIds.Contains(order.Id) && order.Status != OrderStatus.Pending)
            .CountAsync();

        nonPending.ShouldBe(0);
    }

    [Fact]
    public async Task A_promotion_never_leaves_a_status_change_without_its_audit_entry()
    {
        // FR-6.5. Before this was transactional, the claim committed the status change
        // on its own and the history row was written afterwards, so a crash in between
        // produced a Processing order with no audit trail.
        var alice = factory.CreateAliceClient();

        for (var i = 0; i < 3; i++)
        {
            await alice.CreateOrderAsync(DatabaseSeeder.MouseId);
        }

        await using (var scope = factory.CreateAsyncScope())
        {
            var promotion = scope.ServiceProvider
                .GetRequiredService<Application.Orders.OrderPromotionService>();

            await promotion.PromotePendingOrdersAsync(batchSize: 100, maxBatches: 10);
        }

        await using var verifyScope = factory.CreateAsyncScope();
        var dbContext = verifyScope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();

        // Compared in memory rather than as a correlated subquery: the assertion is
        // about set membership, and doing it here removes any doubt about how the
        // provider translated it.
        var processingIds = await dbContext.Orders
            .AsNoTracking()
            .Where(candidate => candidate.Status == OrderStatus.Processing)
            .Select(candidate => candidate.Id)
            .ToListAsync();

        var auditedIds = await dbContext.OrderStatusHistory
            .AsNoTracking()
            .Where(history => history.ToStatus == OrderStatus.Processing)
            .Select(history => history.OrderId)
            .ToListAsync();

        var orphans = processingIds.Except(auditedIds).ToList();

        orphans.ShouldBeEmpty("no order may be Processing without a recorded transition");
    }

    [Fact]
    public async Task A_promoted_order_is_attributed_to_the_system_actor_exactly_once()
    {
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        await using (var scope = factory.CreateAsyncScope())
        {
            var promotion = scope.ServiceProvider
                .GetRequiredService<Application.Orders.OrderPromotionService>();

            await promotion.PromotePendingOrdersAsync(batchSize: 100, maxBatches: 10);
        }

        var reread = await (await alice.GetAsync(
            new Uri($"/api/v1/orders/{order.Id}", UriKind.Relative))).ReadOrderAsync();

        reread.Status.ShouldBe("PROCESSING");

        var promotions = reread.StatusHistory
            .Where(entry => entry.FromStatus == "PENDING" && entry.ToStatus == "PROCESSING")
            .ToList();

        promotions.ShouldHaveSingleItem();
        promotions[0].ChangedBy.ShouldBe("SYSTEM");
    }
}
