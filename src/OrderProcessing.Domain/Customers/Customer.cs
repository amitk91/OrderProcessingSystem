namespace OrderProcessing.Domain.Customers;

/// <summary>
/// A customer who can place orders (specification section 5.1).
/// </summary>
/// <remarks>
/// Deliberately thin: authentication credentials are not modelled here because
/// identity is carried in the JWT (section 8.1), and this assignment does not
/// include registration or password management.
/// </remarks>
public sealed class Customer
{
    private Customer()
    {
        // EF Core materialisation.
        Email = null!;
        FullName = null!;
    }

    private Customer(Guid id, string email, string fullName, DateTimeOffset createdAt)
    {
        Id = id;
        Email = email;
        FullName = fullName;
        CreatedAt = createdAt;
    }

    public Guid Id { get; private set; }

    public string Email { get; private set; }

    public string FullName { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Customer Create(Guid id, string email, string fullName, DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);

        if (id == Guid.Empty)
        {
            throw new ArgumentException("Customer id must not be empty.", nameof(id));
        }

        return new Customer(id, email.Trim().ToLowerInvariant(), fullName.Trim(), createdAt);
    }
}
