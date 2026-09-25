using System.Net;
using OrderProcessing.Infrastructure.Persistence;
using OrderProcessing.Integration.Tests.Infrastructure;
using Shouldly;

namespace OrderProcessing.Integration.Tests;

/// <summary>
/// Access-control behaviour (specification section 8), covering test cases 2 and 3
/// of section 12.2.
/// </summary>
/// <remarks>
/// These are the tests that would catch a Broken Object-Level Authorization defect —
/// the top item on the OWASP API Security Top 10, and the failure mode that
/// role-based checks alone do not prevent.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class SecurityTests(ApiFactory factory)
{
    [Fact]
    public async Task A_customer_cannot_read_another_customers_order_and_is_told_it_does_not_exist()
    {
        // Test case 2: 404 rather than 403, so the response does not confirm the
        // order exists and cannot be used to enumerate ids (section 8.4).
        var alice = factory.CreateAliceClient();
        var bob = factory.CreateBobClient();

        var created = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        var response = await bob.GetAsync(new Uri($"/api/v1/orders/{created.Id}", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.StatusCode.ShouldNotBe(HttpStatusCode.Forbidden);

        var body = await response.Content.ReadAsStringAsync();
        body.ShouldNotContain(created.OrderNumber);
    }

    [Fact]
    public async Task A_customer_cannot_widen_their_scope_with_a_customerId_parameter()
    {
        // Test case 3: the query parameter is ignored for customers (FR-4.4).
        var alice = factory.CreateAliceClient();
        var bob = factory.CreateBobClient();

        await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId);
        await bob.CreateOrderAsync(DatabaseSeeder.MouseId);

        var response = await alice.GetAsync(
            new Uri($"/api/v1/orders?customerId={DatabaseSeeder.BobId}", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);

        var page = await response.ReadPagedOrdersAsync();
        page.Items.ShouldAllBe(order => order.CustomerId == DatabaseSeeder.AliceId);
    }

    [Fact]
    public async Task A_customer_cannot_cancel_another_customers_order()
    {
        var alice = factory.CreateAliceClient();
        var bob = factory.CreateBobClient();

        var created = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        var response = await bob.CancelOrderAsync(created.Id);

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);

        // And Alice's order is untouched.
        var reread = await (await alice.GetAsync(
            new Uri($"/api/v1/orders/{created.Id}", UriKind.Relative))).ReadOrderAsync();
        reread.Status.ShouldBe("PENDING");
    }

    [Fact]
    public async Task A_customer_cannot_use_the_admin_status_endpoint()
    {
        // Role-level authorization: 403 here is correct, because the operation is
        // forbidden regardless of ownership, so no existence is disclosed.
        var alice = factory.CreateAliceClient();
        var created = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        var response = await alice.UpdateStatusAsync(created.Id, "DELIVERED");

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_admin_can_read_any_customers_order()
    {
        var alice = factory.CreateAliceClient();
        var admin = factory.CreateAdminClient();

        var created = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        var response = await admin.GetAsync(new Uri($"/api/v1/orders/{created.Id}", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadOrderAsync()).Id.ShouldBe(created.Id);
    }

    [Fact]
    public async Task An_admin_may_filter_by_customer()
    {
        var alice = factory.CreateAliceClient();
        var bob = factory.CreateBobClient();
        var admin = factory.CreateAdminClient();

        await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId);
        await bob.CreateOrderAsync(DatabaseSeeder.MouseId);

        var page = await (await admin.GetAsync(
            new Uri($"/api/v1/orders?customerId={DatabaseSeeder.BobId}&pageSize=100", UriKind.Relative)))
            .ReadPagedOrdersAsync();

        page.Items.ShouldNotBeEmpty();
        page.Items.ShouldAllBe(order => order.CustomerId == DatabaseSeeder.BobId);
    }

    [Fact]
    public async Task An_unauthenticated_request_is_rejected()
    {
        using var anonymous = factory.CreateClient();

        var response = await anonymous.GetAsync(new Uri("/api/v1/orders", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task An_order_is_attributed_to_the_token_subject_and_not_to_request_data()
    {
        // FR-1.13: there is no customerId field on the request contract, and the
        // order must belong to the authenticated caller regardless.
        var alice = factory.CreateAliceClient();

        var created = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();

        created.CustomerId.ShouldBe(DatabaseSeeder.AliceId);
    }

    [Fact]
    public async Task Health_endpoints_are_reachable_without_a_token()
    {
        using var anonymous = factory.CreateClient();

        (await anonymous.GetAsync(new Uri("/health/live", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await anonymous.GetAsync(new Uri("/health/ready", UriKind.Relative)))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
