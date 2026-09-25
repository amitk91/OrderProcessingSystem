using System.Net;
using OrderProcessing.Infrastructure.Persistence;
using OrderProcessing.Integration.Tests.Infrastructure;
using Shouldly;

namespace OrderProcessing.Integration.Tests;

/// <summary>
/// Concurrency behaviour of order creation (FR-1.10, FR-1.12).
/// </summary>
/// <remarks>
/// A sequential retry of an idempotent request is the easy case and was always handled.
/// These tests cover the genuine race: several requests in flight at once, which is
/// exactly the situation an idempotency key exists to survive — a double-tapped button
/// or a client retrying before the first response arrives.
/// </remarks>
[Collection(nameof(ApiCollection))]
public sealed class OrderCreationConcurrencyTests(ApiFactory factory)
{
    [Fact]
    public async Task Concurrent_requests_with_the_same_idempotency_key_all_return_the_same_order()
    {
        // The read-then-write check cannot prevent this on its own: every request can
        // miss the read before any of them writes. The unique index is what actually
        // enforces it, so the violation must be handled as "someone else created it"
        // rather than surfacing as a failure.
        var alice = factory.CreateAliceClient();
        var key = $"race-{Guid.CreateVersion7():N}";

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ =>
                alice.CreateOrderAsync(DatabaseSeeder.KeyboardId, 1, key)));

        foreach (var response in responses)
        {
            response.StatusCode.ShouldBeOneOf(HttpStatusCode.Created, HttpStatusCode.OK);
        }

        var orders = await Task.WhenAll(responses.Select(response => response.ReadOrderAsync()));

        orders.Select(order => order.Id).Distinct().Count().ShouldBe(
            1,
            "an idempotency key must yield exactly one order however many requests race");

        // And exactly one of them actually created it.
        responses.Count(response => response.StatusCode == HttpStatusCode.Created).ShouldBe(1);
    }

    [Fact]
    public async Task Concurrent_requests_with_different_keys_all_succeed()
    {
        // The guard for the fix: handling the duplicate-key race must not swallow
        // legitimate concurrent creates.
        var alice = factory.CreateAliceClient();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(i =>
                alice.CreateOrderAsync(DatabaseSeeder.MouseId, 1, $"unique-{Guid.CreateVersion7():N}-{i}")));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Created);

        var orders = await Task.WhenAll(responses.Select(response => response.ReadOrderAsync()));
        orders.Select(order => order.Id).Distinct().Count().ShouldBe(8);
    }

    [Fact]
    public async Task Concurrent_creates_without_a_key_produce_distinct_order_numbers()
    {
        // Order numbers are allocated as "highest for this year, plus one". Concurrent
        // callers can read the same maximum, so the unique index on OrderNumber can be
        // violated by an honest race — which must be retried, not reported as an error.
        var alice = factory.CreateAliceClient();

        var responses = await Task.WhenAll(
            Enumerable.Range(0, 8).Select(_ => alice.CreateOrderAsync(DatabaseSeeder.MouseId)));

        responses.ShouldAllBe(response => response.StatusCode == HttpStatusCode.Created);

        var orders = await Task.WhenAll(responses.Select(response => response.ReadOrderAsync()));

        orders.Select(order => order.OrderNumber).Distinct().Count().ShouldBe(
            8,
            "every order must receive a distinct order number");
    }

    [Fact]
    public async Task A_key_reused_concurrently_with_a_different_payload_is_still_rejected()
    {
        // The idempotency contract must not weaken under concurrency: same key plus a
        // different payload is a client error whether or not requests overlap.
        var alice = factory.CreateAliceClient();
        var key = $"mismatch-{Guid.CreateVersion7():N}";

        await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId, 1, key);

        var conflicting = await Task.WhenAll(
            Enumerable.Range(0, 4).Select(_ =>
                alice.CreateOrderAsync(DatabaseSeeder.KeyboardId, 99, key)));

        conflicting.ShouldAllBe(
            response => response.StatusCode == HttpStatusCode.UnprocessableEntity);
    }
}
