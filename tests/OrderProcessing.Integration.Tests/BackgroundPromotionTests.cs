using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Domain.Orders;
using OrderProcessing.Infrastructure.Persistence;
using OrderProcessing.Application.Orders;
using OrderProcessing.Integration.Tests.Infrastructure;
using Shouldly;

namespace OrderProcessing.Integration.Tests;

/// <summary>
/// Background promotion job behaviour (specification section 9, FR-6), covering test
/// cases 5 and 9 of section 12.2.
/// </summary>
/// <remarks>
/// The job is invoked directly rather than through its timer, so the tests control
/// exactly when a run happens. The five-minute schedule itself is verified separately
/// with a fake <see cref="TimeProvider"/>, without waiting five minutes.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class BackgroundPromotionTests(ApiFactory factory)
{
    [Fact]
    public async Task Pending_orders_are_promoted_to_processing()
    {
        // FR-6.2
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        await RunPromotionAsync();

        var reread = await (await alice.GetAsync(
            new Uri($"/api/v1/orders/{order.Id}", UriKind.Relative))).ReadOrderAsync();

        reread.Status.ShouldBe("PROCESSING");
    }

    [Fact]
    public async Task A_promotion_is_recorded_in_the_audit_trail_as_a_system_action()
    {
        // FR-6.5
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.MouseId)).ReadOrderAsync();

        await RunPromotionAsync();

        var reread = await (await alice.GetAsync(
            new Uri($"/api/v1/orders/{order.Id}", UriKind.Relative))).ReadOrderAsync();

        var promotion = reread.StatusHistory.Single(entry =>
            entry.FromStatus == "PENDING" && entry.ToStatus == "PROCESSING");

        promotion.ChangedBy.ShouldBe("SYSTEM");
    }

    [Fact]
    public async Task A_cancelled_order_is_never_promoted()
    {
        // FR-6.6: the claim statement re-asserts the pending status, so an order
        // cancelled before the run is skipped.
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();
        await alice.CancelOrderAsync(order.Id);

        await RunPromotionAsync();

        var reread = await (await alice.GetAsync(
            new Uri($"/api/v1/orders/{order.Id}", UriKind.Relative))).ReadOrderAsync();

        reread.Status.ShouldBe("CANCELLED");
    }

    [Fact]
    public async Task Orders_already_processing_are_not_claimed_again()
    {
        var alice = factory.CreateAliceClient();
        var admin = factory.CreateAdminClient();

        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.MonitorId)).ReadOrderAsync();
        await admin.UpdateStatusAsync(order.Id, "PROCESSING");

        await RunPromotionAsync();

        var reread = await (await alice.GetAsync(
            new Uri($"/api/v1/orders/{order.Id}", UriKind.Relative))).ReadOrderAsync();

        // One promotion only: the admin's. The job must not have added a second.
        reread.StatusHistory.Count(entry => entry.ToStatus == "PROCESSING").ShouldBe(1);
    }

    [Fact]
    public async Task A_run_with_no_pending_orders_does_nothing()
    {
        await RunPromotionAsync();

        var result = await RunPromotionAsync();

        result.Claimed.ShouldBe(0);
        result.Promoted.ShouldBe(0);
        result.Failed.ShouldBe(0);
    }

    [Fact]
    public async Task Claiming_respects_the_batch_size()
    {
        // FR-6.3: bounded batches, so a large backlog cannot be loaded wholesale.
        var alice = factory.CreateAliceClient();
        await RunPromotionAsync();

        for (var i = 0; i < 5; i++)
        {
            await alice.CreateOrderAsync(DatabaseSeeder.MouseId);
        }

        await using var scope = factory.CreateAsyncScope();
        var claimer = scope.ServiceProvider.GetRequiredService<IPendingOrderClaimer>();

        var claimed = await claimer.ClaimPendingOrdersAsync(2, DateTimeOffset.UtcNow);

        claimed.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Concurrent_workers_never_claim_the_same_order_twice()
    {
        // Test case 5 of section 12.2. Four workers race over one backlog; the union
        // of their claims must contain no duplicates and must cover every order.
        var alice = factory.CreateAliceClient();
        await RunPromotionAsync();

        const int orderCount = 24;
        for (var i = 0; i < orderCount; i++)
        {
            await alice.CreateOrderAsync(DatabaseSeeder.MouseId);
        }

        var claimTasks = Enumerable.Range(0, 4).Select(async _ =>
        {
            var claims = new List<Guid>();
            await using var scope = factory.CreateAsyncScope();
            var claimer = scope.ServiceProvider.GetRequiredService<IPendingOrderClaimer>();

            // Bounded rather than while(true): if the claim ever stops transitioning
            // rows, an unbounded loop would hang the suite instead of failing it.
            // A broken implementation should produce a red test, not a stuck build.
            const int maxIterations = 100;

            for (var iteration = 0; iteration < maxIterations; iteration++)
            {
                var batch = await claimer.ClaimPendingOrdersAsync(3, DateTimeOffset.UtcNow);
                if (batch.Count == 0)
                {
                    return claims;
                }

                claims.AddRange(batch);
            }

            throw new InvalidOperationException(
                $"Claiming did not drain after {maxIterations} iterations; " +
                "the claim statement is probably not transitioning rows.");
        });

        var allClaims = (await Task.WhenAll(claimTasks)).SelectMany(claims => claims).ToList();

        allClaims.Count.ShouldBe(orderCount);
        allClaims.Distinct().Count().ShouldBe(orderCount, "no order may be claimed twice");
    }

    [Fact]
    public async Task No_pending_orders_remain_after_a_run()
    {
        var alice = factory.CreateAliceClient();

        for (var i = 0; i < 3; i++)
        {
            await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId);
        }

        await RunPromotionAsync();

        await using var scope = factory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();

        var stillPending = await dbContext.Orders
            .AsNoTracking()
            .CountAsync(order => order.Status == OrderStatus.Pending);

        stillPending.ShouldBe(0);
    }

    [Fact]
    public async Task The_claim_increments_the_concurrency_token()
    {
        // The raw SQL claim must keep the version column consistent with writes made
        // through the aggregate, or the next optimistic-concurrency check would be
        // comparing against a stale value.
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        await using var beforeScope = factory.CreateAsyncScope();
        var versionBefore = await beforeScope.ServiceProvider
            .GetRequiredService<OrderProcessingDbContext>().Orders
            .AsNoTracking()
            .Where(candidate => candidate.Id == order.Id)
            .Select(candidate => candidate.Version)
            .SingleAsync();

        await RunPromotionAsync();

        await using var afterScope = factory.CreateAsyncScope();
        var versionAfter = await afterScope.ServiceProvider
            .GetRequiredService<OrderProcessingDbContext>().Orders
            .AsNoTracking()
            .Where(candidate => candidate.Id == order.Id)
            .Select(candidate => candidate.Version)
            .SingleAsync();

        versionAfter.ShouldBe(versionBefore + 1);
    }

    private async Task<PromotionRunResult> RunPromotionAsync()
    {
        await using var scope = factory.CreateAsyncScope();
        var service = scope.ServiceProvider.GetRequiredService<OrderPromotionService>();

        return await service.PromotePendingOrdersAsync(batchSize: 100, maxBatches: 50);
    }
}
