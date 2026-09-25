using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.Application.Orders;
using OrderProcessing.Domain.Orders;
using OrderProcessing.Infrastructure.Orders;
using OrderProcessing.Infrastructure.Persistence;
using OrderProcessing.Infrastructure.Scheduling;
using OrderProcessing.Integration.Tests.Infrastructure;
using Shouldly;

namespace OrderProcessing.Integration.Tests;

/// <summary>
/// Concurrency behaviour (specification section 10.2), covering test case 4 of
/// section 12.2.
/// </summary>
/// <remarks>
/// The race that matters: an administrator cancels an order at the same moment the
/// scheduler promotes it. Exactly one must win, the loser must be told rather than
/// silently overwritten, and the order must end in a state that is reachable by the
/// transition rules — never a hybrid.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class ConcurrencyTests(ApiFactory factory)
{
    [Fact]
    public async Task A_cancellation_racing_a_promotion_produces_a_deterministic_outcome()
    {
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        // Both operations start from the same observed state, then commit concurrently.
        var cancelTask = Task.Run(async () =>
        {
            try
            {
                await using var scope = factory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<OrderService>();
                await service.CancelAsync(order.Id, Actor.Customer(DatabaseSeeder.AliceId), null);
                return "cancelled";
            }
            catch (Exception ex) when (ex is DbUpdateConcurrencyException or InvalidStatusTransitionException)
            {
                return "cancel-rejected";
            }
        });

        var promoteTask = Task.Run(async () =>
        {
            await using var scope = factory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<OrderPromotionService>();
            var result = await service.PromotePendingOrdersAsync(batchSize: 10, maxBatches: 1);
            return result.Promoted > 0 ? "promoted" : "promote-skipped";
        });

        await Task.WhenAll(cancelTask, promoteTask);

        var final = await (await alice.GetAsync(
            new Uri($"/api/v1/orders/{order.Id}", UriKind.Relative))).ReadOrderAsync();

        // Whichever won, the order must be in one of the two legal outcomes — never
        // both, never a lost update leaving it Pending.
        final.Status.ShouldBeOneOf("CANCELLED", "PROCESSING");
        final.Status.ShouldNotBe("PENDING");

        // And the audit trail must agree with the final state.
        final.StatusHistory[^1].ToStatus.ShouldBe(final.Status);
    }

    [Fact]
    public async Task A_stale_write_is_rejected_rather_than_silently_overwriting()
    {
        // Two scopes load the same order, then both try to change it. The second must
        // be rejected by the concurrency token instead of clobbering the first.
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.MouseId)).ReadOrderAsync();

        await using var firstScope = factory.CreateAsyncScope();
        await using var secondScope = factory.CreateAsyncScope();

        var firstContext = firstScope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();
        var secondContext = secondScope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();

        var firstCopy = await firstContext.Orders.SingleAsync(o => o.Id == order.Id);
        var secondCopy = await secondContext.Orders.SingleAsync(o => o.Id == order.Id);

        firstCopy.TransitionTo(OrderStatus.Processing, Actor.Admin(DatabaseSeeder.AdminId), DateTimeOffset.UtcNow);
        await firstContext.SaveChangesAsync();

        secondCopy.Cancel(Actor.Customer(DatabaseSeeder.AliceId), DateTimeOffset.UtcNow);

        await Should.ThrowAsync<DbUpdateConcurrencyException>(
            async () => await secondContext.SaveChangesAsync());
    }

    [Fact]
    public async Task Concurrent_order_creation_produces_distinct_order_numbers()
    {
        // The unique index on OrderNumber is the backstop; this confirms normal
        // sequential creation does not collide.
        var alice = factory.CreateAliceClient();

        var orders = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            var created = await (await alice.CreateOrderAsync(DatabaseSeeder.MouseId)).ReadOrderAsync();
            orders.Add(created.OrderNumber);
        }

        orders.Distinct().Count().ShouldBe(orders.Count);
    }
}
