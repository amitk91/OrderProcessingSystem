using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Domain.Common;
using OrderProcessing.Domain.Products;
using OrderProcessing.Infrastructure.Persistence;
using OrderProcessing.Integration.Tests.Infrastructure;
using Shouldly;

namespace OrderProcessing.Integration.Tests;

/// <summary>
/// Currency handling across an order (FR-1.5a).
/// </summary>
/// <remarks>
/// The seeded catalogue is entirely USD, so a mixed-currency request cannot be built
/// from it — which is precisely why this path went unexercised. These tests add a
/// euro-priced product so the rejection can be observed over HTTP rather than only in
/// a domain unit test.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class CurrencyTests(ApiFactory factory) : IAsyncLifetime
{
    private static readonly Guid EuroProductId = new("bbbbbbbb-0000-0000-0000-000000000001");

    public async Task InitializeAsync()
    {
        await using var scope = factory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();

        if (await dbContext.Products.FindAsync([EuroProductId]) is null)
        {
            dbContext.Products.Add(Product.Create(
                EuroProductId, "EU-001", "European Adapter", Money.FromDecimal(15.00m, "EUR")));

            await dbContext.SaveChangesAsync();
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Mixing_currencies_in_one_order_is_rejected_with_422_not_500()
    {
        // Previously the currency was taken from whichever product happened to be
        // first, and a later mismatch threw a bare InvalidOperationException — which
        // the handler mapped to 500. Valid products and valid quantities should never
        // produce a server error.
        var alice = factory.CreateAliceClient();

        var response = await alice.CreateOrderAsync(
        [
            (DatabaseSeeder.KeyboardId, 1),  // USD
            (EuroProductId, 1)               // EUR
        ]);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        var problem = await response.ReadProblemAsync();
        problem.Type.ShouldNotBeNull();
        problem.Type.ShouldEndWith("mixed-currency-order");
        problem.Detail.ShouldNotBeNull();
        problem.Detail.ShouldContain("EUR");
        problem.Detail.ShouldContain("USD");
    }

    [Fact]
    public async Task The_rejection_does_not_depend_on_which_currency_comes_first()
    {
        // The old behaviour was order-dependent: the first product silently set the
        // order's currency and everything after it was measured against that.
        var alice = factory.CreateAliceClient();

        var euroFirst = await alice.CreateOrderAsync(
            [(EuroProductId, 1), (DatabaseSeeder.KeyboardId, 1)]);

        var dollarFirst = await alice.CreateOrderAsync(
            [(DatabaseSeeder.KeyboardId, 1), (EuroProductId, 1)]);

        euroFirst.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        dollarFirst.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await euroFirst.ReadProblemAsync()).Detail
            .ShouldBe((await dollarFirst.ReadProblemAsync()).Detail);
    }

    [Fact]
    public async Task An_order_priced_entirely_in_a_non_default_currency_succeeds()
    {
        // The guard for the fix: rejecting mixed currencies must not reject a
        // consistent order that simply is not in USD.
        var alice = factory.CreateAliceClient();

        var order = await (await alice.CreateOrderAsync(EuroProductId, 2)).ReadOrderAsync();

        order.Currency.ShouldBe("EUR");
        order.TotalAmount.ShouldBe(30.00m);
        order.Status.ShouldBe("PENDING");
    }

    [Fact]
    public async Task A_rejected_mixed_currency_order_is_not_persisted()
    {
        var alice = factory.CreateAliceClient();

        var before = (await (await alice.GetAsync(
            new Uri("/api/v1/orders?pageSize=1", UriKind.Relative))).ReadPagedOrdersAsync()).TotalCount;

        await alice.CreateOrderAsync([(DatabaseSeeder.MouseId, 1), (EuroProductId, 1)]);

        var after = (await (await alice.GetAsync(
            new Uri("/api/v1/orders?pageSize=1", UriKind.Relative))).ReadPagedOrdersAsync()).TotalCount;

        after.ShouldBe(before, "a rejected order must leave no trace");
    }
}
