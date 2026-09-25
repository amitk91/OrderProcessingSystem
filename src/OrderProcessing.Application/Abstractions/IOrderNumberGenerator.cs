namespace OrderProcessing.Application.Abstractions;

/// <summary>
/// Generates human-readable order numbers such as <c>ORD-2026-000042</c> (FR-1.10).
/// </summary>
public interface IOrderNumberGenerator
{
    Task<string> NextAsync(CancellationToken cancellationToken = default);
}
