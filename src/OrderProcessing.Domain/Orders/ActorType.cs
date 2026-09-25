namespace OrderProcessing.Domain.Orders;

/// <summary>
/// The kind of actor performing a state transition, per specification section 6.2.
/// Transitions are governed by both the source state and the actor, so this is part
/// of the domain rather than an authorization concern leaking downward.
/// </summary>
public enum ActorType
{
    Customer,
    Admin,

    /// <summary>The background promotion job (specification section 9).</summary>
    System
}
