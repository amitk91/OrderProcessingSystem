using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.Infrastructure.Persistence;
using OrderProcessing.Integration.Tests.Infrastructure;
using Shouldly;

namespace OrderProcessing.Integration.Tests;

/// <summary>
/// End-to-end order lifecycle behaviour over HTTP (specification sections 7 and 11).
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class OrderLifecycleTests(ApiFactory factory)
{
    [Fact]
    public async Task Creating_an_order_returns_201_with_a_location_header()
    {
        var alice = factory.CreateAliceClient();

        var response = await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId, quantity: 2);

        response.StatusCode.ShouldBe(HttpStatusCode.Created);
        response.Headers.Location.ShouldNotBeNull();

        var order = await response.ReadOrderAsync();
        order.Status.ShouldBe("PENDING");
        order.OrderNumber.ShouldStartWith("ORD-");
    }

    [Fact]
    public async Task The_total_is_calculated_server_side_from_catalogue_prices()
    {
        // FR-1.7: keyboard 49.99 x 2 + mouse 19.99 x 1 = 119.97
        var alice = factory.CreateAliceClient();

        var order = await (await alice.CreateOrderAsync(
        [
            (DatabaseSeeder.KeyboardId, 2),
            (DatabaseSeeder.MouseId, 1)
        ])).ReadOrderAsync();

        order.TotalAmount.ShouldBe(119.97m);
        order.Items.Sum(item => item.LineTotal).ShouldBe(119.97m);
    }

    [Fact]
    public async Task A_client_supplied_price_is_ignored()
    {
        // Test case 7 of section 12.2. The request contract has no price field, so this
        // posts raw JSON containing one to prove a hand-crafted request cannot inject it.
        var alice = factory.CreateAliceClient();

        using var content = new StringContent(
            $$"""
            {"items":[{"productId":"{{DatabaseSeeder.KeyboardId}}","quantity":1,"unitPrice":0.01}]}
            """,
            System.Text.Encoding.UTF8,
            "application/json");

        var order = await (await alice.PostAsync(new Uri("/api/v1/orders", UriKind.Relative), content))
            .ReadOrderAsync();

        order.TotalAmount.ShouldBe(49.99m);
        order.Items.Single().UnitPrice.ShouldBe(49.99m);
    }

    [Fact]
    public async Task Duplicate_products_in_one_request_are_merged()
    {
        // FR-1.8
        var alice = factory.CreateAliceClient();

        var order = await (await alice.CreateOrderAsync(
        [
            (DatabaseSeeder.MouseId, 2),
            (DatabaseSeeder.MouseId, 3)
        ])).ReadOrderAsync();

        order.Items.Count.ShouldBe(1);
        order.Items.Single().Quantity.ShouldBe(5);
        order.TotalAmount.ShouldBe(99.95m);
    }

    [Fact]
    public async Task An_order_with_no_items_is_rejected()
    {
        var alice = factory.CreateAliceClient();

        var response = await alice.CreateOrderAsync([]);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task An_inactive_product_cannot_be_ordered()
    {
        // FR-1.3
        var alice = factory.CreateAliceClient();

        var response = await alice.CreateOrderAsync(DatabaseSeeder.DiscontinuedId);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_unknown_product_is_rejected()
    {
        var alice = factory.CreateAliceClient();

        var response = await alice.CreateOrderAsync(Guid.CreateVersion7());

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Repeating_a_request_with_the_same_idempotency_key_returns_the_original_order()
    {
        // Test case 6 of section 12.2.
        var alice = factory.CreateAliceClient();
        var key = $"key-{Guid.CreateVersion7():N}";

        var first = await alice.CreateOrderAsync(DatabaseSeeder.MonitorId, 1, key);
        var second = await alice.CreateOrderAsync(DatabaseSeeder.MonitorId, 1, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        second.StatusCode.ShouldBe(HttpStatusCode.OK);

        (await first.ReadOrderAsync()).Id.ShouldBe((await second.ReadOrderAsync()).Id);
    }

    [Fact]
    public async Task Reusing_an_idempotency_key_with_a_different_payload_is_rejected()
    {
        var alice = factory.CreateAliceClient();
        var key = $"key-{Guid.CreateVersion7():N}";

        await alice.CreateOrderAsync(DatabaseSeeder.MonitorId, 1, key);
        var conflicting = await alice.CreateOrderAsync(DatabaseSeeder.MonitorId, 5, key);

        conflicting.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task An_unknown_order_returns_404()
    {
        var alice = factory.CreateAliceClient();

        var response = await alice.GetAsync(
            new Uri($"/api/v1/orders/{Guid.CreateVersion7()}", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task An_illegal_transition_returns_409_and_lists_what_was_permitted()
    {
        // FR-3.3
        var alice = factory.CreateAliceClient();
        var admin = factory.CreateAdminClient();

        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        var response = await admin.UpdateStatusAsync(order.Id, "DELIVERED");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);

        var problem = await response.ReadProblemAsync();
        problem.PermittedTransitions.ShouldNotBeNull();
        problem.PermittedTransitions.ShouldContain("PROCESSING");
        problem.PermittedTransitions.ShouldContain("CANCELLED");
    }

    [Fact]
    public async Task An_admin_can_walk_an_order_through_the_full_lifecycle()
    {
        var alice = factory.CreateAliceClient();
        var admin = factory.CreateAdminClient();

        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        foreach (var status in new[] { "PROCESSING", "SHIPPED", "DELIVERED" })
        {
            var response = await admin.UpdateStatusAsync(order.Id, status);
            response.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await response.ReadOrderAsync()).Status.ShouldBe(status);
        }

        var final = await (await alice.GetAsync(
            new Uri($"/api/v1/orders/{order.Id}", UriKind.Relative))).ReadOrderAsync();

        // Creation plus three transitions.
        final.StatusHistory.Count.ShouldBe(4);
    }

    [Fact]
    public async Task A_customer_can_cancel_their_own_pending_order()
    {
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        var response = await alice.CancelOrderAsync(order.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cancelled = await response.ReadOrderAsync();
        cancelled.Status.ShouldBe("CANCELLED");
        cancelled.CancelledBy.ShouldBe("CUSTOMER");
        cancelled.CancelledAt.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_customer_cannot_cancel_once_the_order_is_processing()
    {
        // FR-5.2, the rule the brief states explicitly.
        var alice = factory.CreateAliceClient();
        var admin = factory.CreateAdminClient();

        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();
        await admin.UpdateStatusAsync(order.Id, "PROCESSING");

        var response = await alice.CancelOrderAsync(order.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task An_admin_can_cancel_a_processing_order_with_a_reason()
    {
        // FR-5.3
        var alice = factory.CreateAliceClient();
        var admin = factory.CreateAdminClient();

        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();
        await admin.UpdateStatusAsync(order.Id, "PROCESSING");

        var response = await admin.CancelOrderAsync(order.Id, "Payment failed on capture");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var cancelled = await response.ReadOrderAsync();
        cancelled.Status.ShouldBe("CANCELLED");
        cancelled.CancelledBy.ShouldBe("ADMIN");
        cancelled.CancellationReason.ShouldBe("Payment failed on capture");
    }

    [Fact]
    public async Task An_admin_cancellation_without_a_reason_is_rejected()
    {
        // FR-5.4
        var alice = factory.CreateAliceClient();
        var admin = factory.CreateAdminClient();

        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        var response = await admin.CancelOrderAsync(order.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Re_cancelling_is_a_no_op_that_preserves_the_original_audit_data()
    {
        // FR-5.7
        var alice = factory.CreateAliceClient();
        var admin = factory.CreateAdminClient();

        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();
        await alice.CancelOrderAsync(order.Id);

        var response = await admin.CancelOrderAsync(order.Id, "Admin override attempt");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var reread = await response.ReadOrderAsync();
        reread.CancelledBy.ShouldBe("CUSTOMER");
        reread.CancellationReason.ShouldBeNull();
        reread.StatusHistory.Count(entry => entry.ToStatus == "CANCELLED").ShouldBe(1);
    }

    [Fact]
    public async Task A_shipped_order_cannot_be_cancelled()
    {
        var alice = factory.CreateAliceClient();
        var admin = factory.CreateAdminClient();

        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();
        await admin.UpdateStatusAsync(order.Id, "PROCESSING");
        await admin.UpdateStatusAsync(order.Id, "SHIPPED");

        var response = await admin.CancelOrderAsync(order.Id, "Too late");

        response.StatusCode.ShouldBe(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Changing_a_catalogue_price_does_not_alter_an_existing_order()
    {
        // Test case 8 of section 12.2: order items hold a price snapshot.
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.HeadsetId, 2)).ReadOrderAsync();
        order.TotalAmount.ShouldBe(259.00m);

        await using (var scope = factory.CreateAsyncScope())
        {
            var dbContext = scope.ServiceProvider
                .GetRequiredService<OrderProcessingDbContext>();

            var product = await dbContext.Products.FindAsync([DatabaseSeeder.HeadsetId]);
            product!.ChangePrice(Domain.Common.Money.FromDecimal(999.99m, "USD"));
            await dbContext.SaveChangesAsync();
        }

        try
        {
            var reread = await (await alice.GetAsync(
                new Uri($"/api/v1/orders/{order.Id}", UriKind.Relative))).ReadOrderAsync();

            reread.TotalAmount.ShouldBe(259.00m);
            reread.Items.Single().UnitPrice.ShouldBe(129.50m);
        }
        finally
        {
            // Restore, since the catalogue is shared across the collection.
            await using var scope = factory.CreateAsyncScope();
            var dbContext = scope.ServiceProvider
                .GetRequiredService<OrderProcessingDbContext>();

            var product = await dbContext.Products.FindAsync([DatabaseSeeder.HeadsetId]);
            product!.ChangePrice(Domain.Common.Money.FromDecimal(129.50m, "USD"));
            await dbContext.SaveChangesAsync();
        }
    }
}
