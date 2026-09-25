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

