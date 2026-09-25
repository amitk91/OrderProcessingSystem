namespace OrderProcessing.Domain.Common;

/// <summary>
/// Base type for violations of a domain rule. These represent a request that was
/// well-formed but conflicts with the current state of the system, and are mapped
/// to HTTP responses at the API boundary rather than in the domain itself.
/// </summary>
public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message)
    {
    }

    protected DomainException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Stable, machine-readable identifier used to build the RFC 7807 "type" URI
    /// (specification section 11.3). Kept separate from the message so clients can
    /// branch on the code without parsing prose.
    /// </summary>
    public abstract string ErrorCode { get; }
}
