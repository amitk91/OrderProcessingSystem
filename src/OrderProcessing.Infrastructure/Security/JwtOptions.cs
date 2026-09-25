using System.ComponentModel.DataAnnotations;

namespace OrderProcessing.Infrastructure.Security;

/// <summary>
/// JWT bearer settings (specification section 8.1).
/// </summary>
public sealed class JwtOptions
{
    public const string SectionName = "Jwt";

    [Required]
    public string Issuer { get; set; } = "order-processing";

    [Required]
    public string Audience { get; set; } = "order-processing-api";

    /// <summary>
    /// Symmetric signing key. Supplied from configuration, and required to be
    /// overridden outside Development — the default below is a placeholder that the
    /// host refuses to start with in Production.
    /// </summary>
    [Required]
    [MinLength(32)]
    public string SigningKey { get; set; } = "dev-only-signing-key-change-me-0123456789";

    public TimeSpan TokenLifetime { get; set; } = TimeSpan.FromHours(8);
}

/// <summary>
/// Claim types and role names used across the API.
/// </summary>
public static class AuthConstants
{
    public const string CustomerRole = "Customer";
    public const string AdminRole = "Admin";

    /// <summary>Policy requiring the caller to be an administrator.</summary>
    public const string AdminOnlyPolicy = "AdminOnly";
}
