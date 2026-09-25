using OrderProcessing.Domain.Orders;
using Shouldly;

namespace OrderProcessing.Domain.Tests.Orders;

/// <summary>
/// Exhaustive verification of the transition matrix in specification section 6.2
/// (test case 1 of section 12.2).
/// </summary>
/// <remarks>
/// Every (actor x from-state x to-state) combination is generated and asserted —
/// 3 x 5 x 5 = 75 cases — rather than hand-picking interesting ones. Enumerating
/// the whole space means a rule added to the production table without a
/// corresponding decision here fails the build, which is the property that makes
/// this suite worth having.
///
/// <see cref="ExpectedAllowed"/> is written out longhand on purpose. Deriving it
/// from <c>OrderStatusTransitions</c> would make the test tautological: it would
/// pass for any implementation, including a wrong one.
/// </remarks>
public sealed class OrderStatusTransitionsTests
{
    /// <summary>
    /// The specification's matrix, restated independently of the implementation.
    /// </summary>
    private static readonly HashSet<(ActorType Actor, OrderStatus From, OrderStatus To)> ExpectedAllowed =
    [
        // Customer: may only abandon an order not yet worked on (section 6.3).
        (ActorType.Customer, OrderStatus.Pending, OrderStatus.Cancelled),

        // Admin: drives fulfilment, and may cancel up to the point of dispatch.
        (ActorType.Admin, OrderStatus.Pending, OrderStatus.Processing),
        (ActorType.Admin, OrderStatus.Pending, OrderStatus.Cancelled),
        (ActorType.Admin, OrderStatus.Processing, OrderStatus.Shipped),
        (ActorType.Admin, OrderStatus.Processing, OrderStatus.Cancelled),
        (ActorType.Admin, OrderStatus.Shipped, OrderStatus.Delivered),

        // System (background job): promotion only.
        (ActorType.System, OrderStatus.Pending, OrderStatus.Processing)
    ];

    public static TheoryData<ActorType, OrderStatus, OrderStatus> AllCombinations()
    {
        var data = new TheoryData<ActorType, OrderStatus, OrderStatus>();

        foreach (var actor in Enum.GetValues<ActorType>())
        {
            foreach (var from in Enum.GetValues<OrderStatus>())
            {
                foreach (var to in Enum.GetValues<OrderStatus>())
                {
                    data.Add(actor, from, to);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void IsAllowed_matches_the_specified_matrix(ActorType actor, OrderStatus from, OrderStatus to)
    {
        var expected = ExpectedAllowed.Contains((actor, from, to));

        OrderStatusTransitions.IsAllowed(actor, from, to).ShouldBe(
            expected,
            $"transition {from} -> {to} as {actor} should be {(expected ? "allowed" : "rejected")}");
    }

    [Theory]
    [MemberData(nameof(AllCombinations))]
    public void EnsureAllowed_throws_exactly_when_the_transition_is_rejected(
        ActorType actor,
        OrderStatus from,
        OrderStatus to)
    {
        var shouldSucceed = ExpectedAllowed.Contains((actor, from, to));

        if (shouldSucceed)
        {
            Should.NotThrow(() => OrderStatusTransitions.EnsureAllowed(actor, from, to));
            return;
        }

        var exception = Should.Throw<InvalidStatusTransitionException>(
            () => OrderStatusTransitions.EnsureAllowed(actor, from, to));

        exception.From.ShouldBe(from);
        exception.To.ShouldBe(to);
        exception.Actor.ShouldBe(actor);
        exception.Permitted.ShouldNotContain(to);
    }

    [Fact]
    public void The_matrix_permits_exactly_seven_transitions_in_total()
    {
        // Guards against a rule being widened accidentally. If this count changes,
        // it should be because someone deliberately changed the policy.
        var actual =
            from actor in Enum.GetValues<ActorType>()
            from origin in Enum.GetValues<OrderStatus>()
            from destination in Enum.GetValues<OrderStatus>()
            where OrderStatusTransitions.IsAllowed(actor, origin, destination)
            select (actor, origin, destination);

        actual.Count().ShouldBe(ExpectedAllowed.Count);
    }

    [Theory]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    public void Terminal_states_admit_no_transitions_for_any_actor(OrderStatus terminal)
    {
        OrderStatusTransitions.IsTerminal(terminal).ShouldBeTrue();

        foreach (var actor in Enum.GetValues<ActorType>())
        {
            OrderStatusTransitions.PermittedFrom(actor, terminal).ShouldBeEmpty();
        }
    }

    [Theory]
    [InlineData(OrderStatus.Pending)]
    [InlineData(OrderStatus.Processing)]
    [InlineData(OrderStatus.Shipped)]
    public void Non_terminal_states_are_not_reported_as_terminal(OrderStatus status) =>
        OrderStatusTransitions.IsTerminal(status).ShouldBeFalse();

    [Fact]
    public void A_customer_cannot_cancel_an_order_that_is_already_being_processed()
    {
        // FR-5.2: the brief's rule, asserted explicitly because it is the one
        // customer-facing restriction the assignment calls out by name.
        OrderStatusTransitions
            .IsAllowed(ActorType.Customer, OrderStatus.Processing, OrderStatus.Cancelled)
            .ShouldBeFalse();
    }

    [Fact]
    public void An_admin_can_cancel_an_order_that_is_already_being_processed()
    {
        // FR-5.3: the deliberate widening beyond the brief, for operational reality.
        OrderStatusTransitions
            .IsAllowed(ActorType.Admin, OrderStatus.Processing, OrderStatus.Cancelled)
            .ShouldBeTrue();
    }

    [Fact]
    public void Nobody_can_cancel_a_shipped_order()
    {
        // Section 6.3: past dispatch this is a returns flow, not a cancellation.
        foreach (var actor in Enum.GetValues<ActorType>())
        {
            OrderStatusTransitions
                .IsAllowed(actor, OrderStatus.Shipped, OrderStatus.Cancelled)
                .ShouldBeFalse($"{actor} must not be able to cancel a shipped order");
        }
    }

    [Fact]
    public void The_background_job_can_only_promote_and_never_cancel()
    {
        OrderStatusTransitions
            .PermittedFrom(ActorType.System, OrderStatus.Pending)
            .ShouldBe([OrderStatus.Processing]);

        foreach (var from in Enum.GetValues<OrderStatus>())
        {
            OrderStatusTransitions
                .IsAllowed(ActorType.System, from, OrderStatus.Cancelled)
                .ShouldBeFalse("the scheduler must never cancel an order");
        }
    }

    [Fact]
    public void Skip_transitions_are_rejected()
    {
        // Pending -> Shipped would bypass Processing entirely.
        foreach (var actor in Enum.GetValues<ActorType>())
        {
            OrderStatusTransitions.IsAllowed(actor, OrderStatus.Pending, OrderStatus.Shipped).ShouldBeFalse();
            OrderStatusTransitions.IsAllowed(actor, OrderStatus.Pending, OrderStatus.Delivered).ShouldBeFalse();
            OrderStatusTransitions.IsAllowed(actor, OrderStatus.Processing, OrderStatus.Delivered).ShouldBeFalse();
        }
    }

    [Fact]
    public void Backward_transitions_are_rejected()
    {
        foreach (var actor in Enum.GetValues<ActorType>())
        {
            OrderStatusTransitions.IsAllowed(actor, OrderStatus.Processing, OrderStatus.Pending).ShouldBeFalse();
            OrderStatusTransitions.IsAllowed(actor, OrderStatus.Shipped, OrderStatus.Processing).ShouldBeFalse();
            OrderStatusTransitions.IsAllowed(actor, OrderStatus.Delivered, OrderStatus.Shipped).ShouldBeFalse();
        }
    }

    [Fact]
    public void Self_transitions_are_not_permitted_by_the_matrix()
    {
        // Same-status requests are handled as an idempotent no-op above the domain
        // (FR-3.5); the matrix itself treats them as movement and rejects them.
        foreach (var actor in Enum.GetValues<ActorType>())
        {
            foreach (var status in Enum.GetValues<OrderStatus>())
            {
                OrderStatusTransitions.IsAllowed(actor, status, status).ShouldBeFalse();
            }
        }
    }
}
