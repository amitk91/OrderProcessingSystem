using System.Net;
using OrderProcessing.Infrastructure.Persistence;
using OrderProcessing.Integration.Tests.Infrastructure;
using Shouldly;

namespace OrderProcessing.Integration.Tests;

/// <summary>
/// Listing, filtering, sorting and pagination (FR-4), covering test case 10 of
/// section 12.2.
/// </summary>
[Collection(nameof(ApiCollection))]
public sealed class OrderListingTests(ApiFactory factory)
{
    [Fact]
    public async Task An_oversized_page_size_is_clamped_rather_than_rejected()
    {
        // FR-4.6: a client asking for too much gets the maximum, not an error.
        var alice = factory.CreateAliceClient();

        var response = await alice.GetAsync(new Uri("/api/v1/orders?pageSize=10000", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadPagedOrdersAsync()).PageSize.ShouldBe(100);
    }

    [Fact]
    public async Task A_non_positive_page_size_falls_back_to_the_default()
    {
        var alice = factory.CreateAliceClient();

        var page = await (await alice.GetAsync(
            new Uri("/api/v1/orders?pageSize=0", UriKind.Relative))).ReadPagedOrdersAsync();

        page.PageSize.ShouldBe(20);
    }

    [Fact]
    public async Task A_page_number_below_one_is_treated_as_the_first_page()
    {
        var alice = factory.CreateAliceClient();

        var page = await (await alice.GetAsync(
            new Uri("/api/v1/orders?page=-5", UriKind.Relative))).ReadPagedOrdersAsync();

        page.Page.ShouldBe(1);
    }

    [Fact]
    public async Task Results_can_be_filtered_by_status()
    {
        // FR-4.2
        var alice = factory.CreateAliceClient();
        var order = await (await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId)).ReadOrderAsync();
        await alice.CancelOrderAsync(order.Id);

        var page = await (await alice.GetAsync(
            new Uri("/api/v1/orders?status=CANCELLED&pageSize=100", UriKind.Relative)))
            .ReadPagedOrdersAsync();

        page.Items.ShouldNotBeEmpty();
        page.Items.ShouldAllBe(candidate => candidate.Status == "CANCELLED");
    }

    [Fact]
    public async Task The_status_filter_is_case_insensitive()
    {
        var alice = factory.CreateAliceClient();

        var response = await alice.GetAsync(new Uri("/api/v1/orders?status=pending", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task An_unrecognised_status_filter_is_rejected_with_guidance()
    {
        var alice = factory.CreateAliceClient();

        var response = await alice.GetAsync(new Uri("/api/v1/orders?status=NONSENSE", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var problem = await response.ReadProblemAsync();
        problem.Detail.ShouldNotBeNull();
        problem.Detail.ShouldContain("PENDING");
    }

    [Fact]
    public async Task Orders_sort_by_total_numerically_and_not_as_text()
    {
        // The defect that drove the integer-minor-unit design (section 5.4): as TEXT,
        // "9.99" sorts after "100.00", so this would silently return a wrong order
        // and wrong pagination.
        var alice = factory.CreateAliceClient();

        // 19.99, 49.99 and 299.00 — the lexicographic and numeric orders differ.
        await alice.CreateOrderAsync(DatabaseSeeder.MouseId);
        await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId);
        await alice.CreateOrderAsync(DatabaseSeeder.MonitorId);

        var page = await (await alice.GetAsync(
            new Uri("/api/v1/orders?sortBy=totalAmount&desc=false&pageSize=100", UriKind.Relative)))
            .ReadPagedOrdersAsync();

        var totals = page.Items.Select(order => order.TotalAmount).ToList();

        totals.ShouldBe(totals.OrderBy(total => total).ToList());
    }

    [Fact]
    public async Task Orders_sort_by_creation_date_descending_by_default()
    {
        // FR-4.7
        var alice = factory.CreateAliceClient();
        await alice.CreateOrderAsync(DatabaseSeeder.MouseId);
        await alice.CreateOrderAsync(DatabaseSeeder.KeyboardId);

        var page = await (await alice.GetAsync(
            new Uri("/api/v1/orders?pageSize=100", UriKind.Relative))).ReadPagedOrdersAsync();

        var dates = page.Items.Select(order => order.CreatedAt).ToList();
        dates.ShouldBe(dates.OrderByDescending(date => date).ToList());
    }

    [Fact]
    public async Task An_unsortable_field_is_rejected()
    {
        var alice = factory.CreateAliceClient();

        var response = await alice.GetAsync(
            new Uri("/api/v1/orders?sortBy=customerId", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Pagination_metadata_is_consistent()
    {
        // FR-4.8
        var bob = factory.CreateBobClient();

        for (var i = 0; i < 5; i++)
        {
            await bob.CreateOrderAsync(DatabaseSeeder.MouseId);
        }

        var page = await (await bob.GetAsync(
            new Uri("/api/v1/orders?page=1&pageSize=2", UriKind.Relative))).ReadPagedOrdersAsync();

        page.Page.ShouldBe(1);
        page.PageSize.ShouldBe(2);
        page.TotalCount.ShouldBeGreaterThanOrEqualTo(5);
        page.TotalPages.ShouldBe((int)Math.Ceiling(page.TotalCount / 2.0));
        page.Items.Count.ShouldBe(2);
    }

    [Fact]
    public async Task Paging_through_results_yields_no_duplicates_and_no_gaps()
    {
        // A stable sort matters here: without a tie-breaker, rows with identical
        // timestamps could appear on two pages or on none.
        var bob = factory.CreateBobClient();

        for (var i = 0; i < 6; i++)
        {
            await bob.CreateOrderAsync(DatabaseSeeder.KeyboardId);
        }

        var total = (await (await bob.GetAsync(
            new Uri("/api/v1/orders?pageSize=100", UriKind.Relative))).ReadPagedOrdersAsync()).TotalCount;

        var seen = new List<Guid>();
        var pageSize = 3;

        for (var page = 1; seen.Count < total; page++)
        {
            var result = await (await bob.GetAsync(
                new Uri($"/api/v1/orders?page={page}&pageSize={pageSize}", UriKind.Relative)))
                .ReadPagedOrdersAsync();

            if (result.Items.Count == 0)
            {
                break;
            }

            seen.AddRange(result.Items.Select(order => order.Id));
        }

        seen.Count.ShouldBe(total);
        seen.Distinct().Count().ShouldBe(total, "paging must not return the same order twice");
    }

    [Fact]
    public async Task An_empty_result_is_an_empty_page_rather_than_a_404()
    {
        // FR-4.9
        var alice = factory.CreateAliceClient();

        var response = await alice.GetAsync(
            new Uri("/api/v1/orders?status=DELIVERED&page=500", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await response.ReadPagedOrdersAsync()).Items.ShouldBeEmpty();
    }
}
