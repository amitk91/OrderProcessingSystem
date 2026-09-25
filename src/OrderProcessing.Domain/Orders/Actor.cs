namespace OrderProcessing.Domain.Orders;

/// <summary>
/// Who is performing an operation: the role, and the user behind it where one exists.
/// </summary>
/// <remarks>
/// Passing this into the aggregate keeps transition rules and audit attribution
/// together, and makes it impossible to change an order's status without recording
/// who did it. The background job uses <see cref="System"/>, which carries no user id.
/// </remarks>
public readonly record struct Actor
{
    private Actor(ActorType type, Guid? userId)
    {
        Type = type;
        UserId = userId;
    }

    public ActorType Type { get; }

    /// <summary>Null for <see cref="ActorType.System"/>; required otherwise.</summary>
    public Guid? UserId { get; }

    /// <summary>The background promotion job (specification section 9).</summary>
    public static Actor System { get; } = new(ActorType.System, userId: null);

    public static Actor Customer(Guid customerId)
    {
        if (customerId == Guid.Empty)
        {
            throw new ArgumentException("Customer id must not be empty.", nameof(customerId));
        }

        return new Actor(ActorType.Customer, customerId);
    }

    public static Actor Admin(Guid adminId)
    {
        if (adminId == Guid.Empty)
        {
            throw new ArgumentException("Admin id must not be empty.", nameof(adminId));
        }

        return new Actor(ActorType.Admin, adminId);
    }

    public bool IsAdmin => Type == ActorType.Admin;

    public bool IsCustomer => Type == ActorType.Customer;

    public bool IsSystem => Type == ActorType.System;
}
