using OrderProcessing.Domain.Orders;
using Shouldly;

namespace OrderProcessing.Domain.Tests.Orders;

/// <summary>
/// Guards the state set defined in specification section 6.1. The set of statuses is
/// part of the published API contract (the <c>status</c> filter in FR-4.2), so adding
/// or renaming one is a breaking change that should fail loudly rather than silently.
/// </summary>
public sealed class OrderStatusTests
{
    [Fact]
    public void OrderStatus_defines_exactly_the_five_specified_states()
    {
        var actual = Enum.GetNames<OrderStatus>();

        actual.ShouldBe(
            ["Pending", "Processing", "Shipped", "Delivered", "Cancelled"],
            ignoreOrder: true);
    }

    [Fact]
    public void ActorType_defines_exactly_the_three_specified_actors()
    {
        var actual = Enum.GetNames<ActorType>();

        actual.ShouldBe(["Customer", "Admin", "System"], ignoreOrder: true);
    }
}
