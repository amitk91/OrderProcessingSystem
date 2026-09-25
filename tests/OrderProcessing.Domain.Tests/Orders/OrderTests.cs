using OrderProcessing.Domain.Common;
using OrderProcessing.Domain.Orders;
using Shouldly;

namespace OrderProcessing.Domain.Tests.Orders;

/// <summary>
/// Behaviour of the <see cref="Order"/> aggregate: creation invariants (FR-1),
/// status transitions (FR-3), cancellation policy (FR-5) and the audit trail.
/// </summary>
public sealed class OrderTests
{
    private static readonly DateTimeOffset Now = new(2026, 1, 15, 10, 0, 0, TimeSpan.Zero);

    public sealed class Creation
    {
        [Fact]
        public void A_new_order_starts_pending()
        {
            var order = new OrderBuilder().Build();

            order.Status.ShouldBe(OrderStatus.Pending);
        }

        [Fact]
        public void The_total_is_the_sum_of_the_line_totals()
        {
            var order = new OrderBuilder()
                .WithItem(49.99m, quantity: 2)
                .WithItem(19.99m, quantity: 1)
                .Build();

            order.TotalAmount.Amount.ShouldBe(119.97m);
            order.TotalAmountMinor.ShouldBe(11997);
        }

        [Fact]
        public void Each_line_total_is_the_unit_price_times_the_quantity()
        {
            var order = new OrderBuilder().WithItem(49.99m, quantity: 3).Build();

            order.Items.Single().LineTotal.Amount.ShouldBe(149.97m);
        }

        [Fact]
        public void An_order_with_no_items_is_rejected()
        {
            // FR-1.2
            Should.Throw<EmptyOrderException>(() => Order.Create(
                Guid.CreateVersion7(),
                "ORD-1",
                Guid.CreateVersion7(),
                lines: [],
                "USD",
                Now));
        }

        [Fact]
        public void Duplicate_products_are_merged_and_their_quantities_summed()
        {
            // FR-1.8: two lines for the same product become one line of quantity 5.
            var productId = Guid.CreateVersion7();
            var order = new OrderBuilder()
                .WithItem(10.00m, quantity: 2, productId: productId)
                .WithItem(10.00m, quantity: 3, productId: productId)
                .Build();

            order.Items.Count.ShouldBe(1);
            order.Items.Single().Quantity.ShouldBe(5);
            order.TotalAmount.Amount.ShouldBe(50.00m);
        }

        [Fact]
        public void Creation_records_an_initial_history_entry()
        {
            // FR-1.11
            var order = new OrderBuilder().Build();

            var entry = order.StatusHistory.ShouldHaveSingleItem();
            entry.FromStatus.ShouldBeNull();
            entry.ToStatus.ShouldBe(OrderStatus.Pending);
        }

        [Fact]
        public void Items_priced_in_a_different_currency_are_rejected()
        {
            var lines = new[]
            {
                new OrderLine(Guid.CreateVersion7(), "Euro item", Money.FromDecimal(10m, "EUR"), 1)
            };

            Should.Throw<InvalidOperationException>(() => Order.Create(
                Guid.CreateVersion7(),
                "ORD-1",
                Guid.CreateVersion7(),
                lines,
                "USD",
                Now));
        }

        [Fact]
        public void A_quantity_below_one_is_rejected()
        {
            // FR-1.4
            var lines = new[]
            {
                new OrderLine(Guid.CreateVersion7(), "Item", Money.FromDecimal(10m, "USD"), 0)
            };

            Should.Throw<ArgumentOutOfRangeException>(() => Order.Create(
                Guid.CreateVersion7(),
                "ORD-1",
                Guid.CreateVersion7(),
                lines,
                "USD",
                Now));
        }

        [Fact]
        public void Changing_a_catalogue_price_afterwards_does_not_alter_the_order()
        {
            // Test case 8 of section 12.2: order items hold a price snapshot, so a
            // later catalogue change must not rewrite history.
            var productId = Guid.CreateVersion7();
            var product = Products.Product.Create(
                productId, "SKU-1", "Widget", Money.FromDecimal(49.99m, "USD"));

            var order = new OrderBuilder()
                .WithLine(new OrderLine(productId, product.Name, product.UnitPrice, 2))
                .Build();

            product.ChangePrice(Money.FromDecimal(99.99m, "USD"));

            order.TotalAmount.Amount.ShouldBe(99.98m);
            order.Items.Single().UnitPrice.Amount.ShouldBe(49.99m);
        }
    }

    public sealed class Transitions
    {
        [Fact]
        public void An_admin_can_advance_a_pending_order_to_processing()
        {
            var order = new OrderBuilder().Build();

            var changed = order.TransitionTo(OrderStatus.Processing, Actor.Admin(Guid.CreateVersion7()), Now);

            changed.ShouldBeTrue();
            order.Status.ShouldBe(OrderStatus.Processing);
        }

        [Fact]
        public void The_background_job_can_promote_a_pending_order()
        {
            // FR-6.2
            var order = new OrderBuilder().Build();

            order.PromoteToProcessing(Now).ShouldBeTrue();

            order.Status.ShouldBe(OrderStatus.Processing);
            order.StatusHistory[^1].ChangedBy.ShouldBe(ActorType.System);
            order.StatusHistory[^1].ChangedByUserId.ShouldBeNull();
        }

        [Fact]
        public void A_transition_to_the_current_status_is_a_no_op()
        {
            // FR-3.5: reported as "nothing changed" rather than rejected.
            var order = new OrderBuilder().Build();
            var historyBefore = order.StatusHistory.Count;

            order.TransitionTo(OrderStatus.Pending, Actor.Admin(Guid.CreateVersion7()), Now).ShouldBeFalse();

            order.Version.ShouldBe(0);
            order.StatusHistory.Count.ShouldBe(historyBefore);
        }

        [Fact]
        public void An_illegal_transition_throws_and_leaves_the_order_untouched()
        {
            var order = new OrderBuilder().Build();

            Should.Throw<InvalidStatusTransitionException>(
                () => order.TransitionTo(OrderStatus.Delivered, Actor.Admin(Guid.CreateVersion7()), Now));

            order.Status.ShouldBe(OrderStatus.Pending);
            order.Version.ShouldBe(0);
            order.StatusHistory.Count.ShouldBe(1);
        }

        [Fact]
        public void Every_successful_transition_appends_to_the_history()
        {
            // FR-3.4
            var order = new OrderBuilder().Build();
            var admin = Actor.Admin(Guid.CreateVersion7());

            order.TransitionTo(OrderStatus.Processing, admin, Now);
            order.TransitionTo(OrderStatus.Shipped, admin, Now.AddHours(1));

            order.StatusHistory.Count.ShouldBe(3);
            order.StatusHistory[^1].FromStatus.ShouldBe(OrderStatus.Processing);
            order.StatusHistory[^1].ToStatus.ShouldBe(OrderStatus.Shipped);        }

        [Fact]
        public void The_version_increments_on_every_state_change()
        {
            // Section 10.2: this is what makes concurrent writes detectable.
            var order = new OrderBuilder().Build();
            var admin = Actor.Admin(Guid.CreateVersion7());

            order.Version.ShouldBe(0);
            order.TransitionTo(OrderStatus.Processing, admin, Now);
            order.Version.ShouldBe(1);
            order.TransitionTo(OrderStatus.Shipped, admin, Now.AddHours(1));
            order.Version.ShouldBe(2);
        }

        [Fact]
        public void UpdatedAt_tracks_the_most_recent_change()
        {
            var order = new OrderBuilder().CreatedAt(Now).Build();
            var later = Now.AddHours(2);

            order.TransitionTo(OrderStatus.Processing, Actor.Admin(Guid.CreateVersion7()), later);

            order.UpdatedAt.ShouldBe(later);
            order.CreatedAt.ShouldBe(Now);
        }
    }

    public sealed class Cancellation
    {
        [Fact]
        public void A_customer_can_cancel_their_pending_order()
        {
            // FR-5.2
            var customerId = Guid.CreateVersion7();
            var order = new OrderBuilder().ForCustomer(customerId).Build();

            order.Cancel(Actor.Customer(customerId), Now);

            order.Status.ShouldBe(OrderStatus.Cancelled);
            order.CancelledBy.ShouldBe(ActorType.Customer);
            order.CancelledAt.ShouldBe(Now);
        }

        [Fact]
        public void A_customer_cannot_cancel_once_processing_has_started()
        {
            // FR-5.2, and the rule the brief calls out explicitly.
            var customerId = Guid.CreateVersion7();
            var order = new OrderBuilder().ForCustomer(customerId).BuildInStatus(OrderStatus.Processing);

            Should.Throw<InvalidStatusTransitionException>(
                () => order.Cancel(Actor.Customer(customerId), Now));

            order.Status.ShouldBe(OrderStatus.Processing);
        }

        [Fact]
        public void An_admin_can_cancel_an_order_that_is_processing()
        {
            // FR-5.3: the deliberate widening beyond the brief.
            var order = new OrderBuilder().BuildInStatus(OrderStatus.Processing);

            order.Cancel(Actor.Admin(Guid.CreateVersion7()), Now, "Payment failed on capture");

            order.Status.ShouldBe(OrderStatus.Cancelled);
            order.CancelledBy.ShouldBe(ActorType.Admin);
            order.CancellationReason.ShouldBe("Payment failed on capture");
        }

        [Fact]
        public void An_admin_must_supply_a_reason()
        {
            // FR-5.4: an override is only defensible if it is attributable.
            var order = new OrderBuilder().Build();

            Should.Throw<CancellationReasonRequiredException>(
                () => order.Cancel(Actor.Admin(Guid.CreateVersion7()), Now));

            order.Status.ShouldBe(OrderStatus.Pending);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_blank_admin_reason_is_treated_as_no_reason(string? reason)
        {
            var order = new OrderBuilder().Build();

            Should.Throw<CancellationReasonRequiredException>(
                () => order.Cancel(Actor.Admin(Guid.CreateVersion7()), Now, reason));
        }

        [Fact]
        public void A_customer_does_not_need_to_supply_a_reason()
        {
            var customerId = Guid.CreateVersion7();
            var order = new OrderBuilder().ForCustomer(customerId).Build();

            Should.NotThrow(() => order.Cancel(Actor.Customer(customerId), Now));

            order.CancellationReason.ShouldBeNull();
        }

        [Fact]
        public void A_shipped_order_cannot_be_cancelled_by_anyone()
        {
            // Section 6.3: past dispatch this is a returns flow.
            var order = new OrderBuilder().BuildInStatus(OrderStatus.Shipped);

            Should.Throw<InvalidStatusTransitionException>(
                () => order.Cancel(Actor.Admin(Guid.CreateVersion7()), Now, "Changed mind"));
        }

        [Fact]
        public void Cancelling_an_already_cancelled_order_preserves_the_original_audit_data()
        {
            // FR-5.7: the second attempt must not overwrite who cancelled it or why.
            var customerId = Guid.CreateVersion7();
            var order = new OrderBuilder().ForCustomer(customerId).Build();
            order.Cancel(Actor.Customer(customerId), Now);

            // Same-status requests short-circuit as an idempotent no-op (FR-3.5),
            // so the later admin attempt changes nothing.
            var changed = order.Cancel(Actor.Admin(Guid.CreateVersion7()), Now.AddHours(1), "Late override");

            changed.ShouldBeFalse();
            order.CancelledBy.ShouldBe(ActorType.Customer);
            order.CancelledAt.ShouldBe(Now);
            order.CancellationReason.ShouldBeNull();
            order.StatusHistory.Count(h => h.ToStatus == OrderStatus.Cancelled).ShouldBe(1);
        }

        [Fact]
        public void Cancellation_is_recorded_in_the_history()
        {
            // FR-5.6
            var adminId = Guid.CreateVersion7();
            var order = new OrderBuilder().Build();

            order.Cancel(Actor.Admin(adminId), Now, "Suspected fraud");

            var entry = order.StatusHistory[^1];
            entry.ToStatus.ShouldBe(OrderStatus.Cancelled);
            entry.ChangedBy.ShouldBe(ActorType.Admin);
            entry.ChangedByUserId.ShouldBe(adminId);
            entry.Reason.ShouldBe("Suspected fraud");
        }

        [Fact]
        public void The_background_job_can_never_cancel_an_order()
        {
            var order = new OrderBuilder().Build();

            Should.Throw<InvalidStatusTransitionException>(
                () => order.Cancel(Actor.System, Now, "automated"));
        }
    }

    public sealed class Ownership
    {
        [Fact]
        public void IsOwnedBy_distinguishes_the_owner_from_everyone_else()
        {
            var owner = Guid.CreateVersion7();
            var order = new OrderBuilder().ForCustomer(owner).Build();

            order.IsOwnedBy(owner).ShouldBeTrue();
            order.IsOwnedBy(Guid.CreateVersion7()).ShouldBeFalse();
        }
    }
}
