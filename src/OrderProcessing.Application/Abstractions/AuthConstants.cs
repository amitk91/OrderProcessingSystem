namespace OrderProcessing.Application.Abstractions;

/// <summary>
/// Role names and authorization policy identifiers.
/// </summary>
/// <remarks>
/// In the application layer because both the API (endpoint attributes) and
/// infrastructure (policy registration) need them, and neither should own a constant
/// the other depends on.
/// </remarks>
public static class AuthConstants
{
    public const string CustomerRole = "Customer";
    public const string AdminRole = "Admin";

    /// <summary>Policy requiring the caller to be an administrator.</summary>
    public const string AdminOnlyPolicy = "AdminOnly";
}
