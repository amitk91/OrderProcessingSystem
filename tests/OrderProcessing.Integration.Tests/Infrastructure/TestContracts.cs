using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OrderProcessing.Integration.Tests.Infrastructure;

/// <summary>Minimal response shapes for asserting on API payloads.</summary>
public sealed record TestOrderItem(
    Guid ProductId,
    string ProductName,
    decimal UnitPrice,
    int Quantity,
    decimal LineTotal);

public sealed record TestStatusHistory(
    string? FromStatus,
    string ToStatus,
    string ChangedBy,
    string? Reason,
    DateTimeOffset ChangedAt);

public sealed record TestOrder(
    Guid Id,
    string OrderNumber,
    Guid CustomerId,
    string Status,
    string Currency,
    decimal TotalAmount,
    IReadOnlyList<TestOrderItem> Items,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CancelledAt,
    string? CancelledBy,
    string? CancellationReason,
    IReadOnlyList<TestStatusHistory> StatusHistory);

public sealed record TestPagedOrders(
    IReadOnlyList<TestOrder> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalPages);

public sealed record TestProblemDetails(
    string? Type,
    string? Title,
    int? Status,
    string? Detail,
    [property: JsonPropertyName("permittedTransitions")]
    IReadOnlyList<string>? PermittedTransitions);

/// <summary>Request helpers keeping the tests readable.</summary>
public static class ApiClientExtensions
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    public static Task<HttpResponseMessage> CreateOrderAsync(
        this HttpClient client,
        IEnumerable<(Guid ProductId, int Quantity)> items,
        string? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders")
        {
            Content = JsonContent.Create(new
            {
                items = items.Select(item => new { productId = item.ProductId, quantity = item.Quantity })
            })
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey);
        }

        return client.SendAsync(request);
    }

    public static Task<HttpResponseMessage> CreateOrderAsync(
        this HttpClient client,
        Guid productId,
        int quantity = 1,
        string? idempotencyKey = null) =>
        client.CreateOrderAsync([(productId, quantity)], idempotencyKey);

    public static Task<HttpResponseMessage> UpdateStatusAsync(
        this HttpClient client,
        Guid orderId,
        string status,
        string? reason = null) =>
        client.PatchAsJsonAsync($"/api/v1/orders/{orderId}/status", new { status, reason });

    public static Task<HttpResponseMessage> CancelOrderAsync(
        this HttpClient client,
        Guid orderId,
        string? reason = null) =>
        client.PostAsJsonAsync($"/api/v1/orders/{orderId}/cancel", new { reason });

    public static async Task<TestOrder> ReadOrderAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var raw = await response.Content.ReadAsStringAsync();

        // Surfacing the real body here turns an opaque deserialisation failure into a
        // readable assertion message when an endpoint returns a problem instead.
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Expected an order but the API returned {(int)response.StatusCode}: {raw}");
        }

        return JsonSerializer.Deserialize<TestOrder>(raw, JsonOptions)
            ?? throw new InvalidOperationException($"Response did not contain an order: {raw}");
    }

    public static async Task<TestPagedOrders> ReadPagedOrdersAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var page = await response.Content.ReadFromJsonAsync<TestPagedOrders>(JsonOptions);
        return page ?? throw new InvalidOperationException("Response did not contain a page of orders.");
    }

    public static async Task<TestProblemDetails> ReadProblemAsync(this HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);

        var problem = await response.Content.ReadFromJsonAsync<TestProblemDetails>(JsonOptions);
        return problem ?? throw new InvalidOperationException("Response did not contain problem details.");
    }
}
