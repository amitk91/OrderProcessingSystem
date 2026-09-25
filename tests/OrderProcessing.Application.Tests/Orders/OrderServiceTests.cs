using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Application.Orders;
using OrderProcessing.Domain.Common;
using OrderProcessing.Domain.Orders;
using OrderProcessing.Domain.Products;
using Shouldly;

namespace OrderProcessing.Application.Tests.Orders;

/// <summary>
/// Unit tests for the order use cases, run entirely against substituted ports.
/// </summary>
/// <remarks>
/// These tests exist only because orchestration lives in the application layer and
/// depends on <see cref="IOrderRepository"/> and <see cref="IProductCatalog"/> rather
/// than on a <c>DbContext</c>. They need no database, no migrations and no host.
///
/// That is the practical payoff of the dependency rule: while these tests were
/// impossible to write, the layering was wrong.
/// </remarks>
public sealed class OrderServiceTests
{
    private static readonly DateTimeOffset Now = new(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly Guid CustomerId = Guid.CreateVersion7();
    private static readonly Guid KeyboardId = Guid.CreateVersion7();
    private static readonly Guid MouseId = Guid.CreateVersion7();

    private readonly IOrderRepository _orders = Substitute.For<IOrderRepository>();
    private readonly IProductCatalog _catalog = Substitute.For<IProductCatalog>();
    private readonly IOrderNumberGenerator _numbers = Substitute.For<IOrderNumberGenerator>();
    private readonly FakeTimeProvider _clock = new(Now);

    public OrderServiceTests()
    {
        _numbers.NextAsync(Arg.Any<CancellationToken>()).Returns("ORD-2026-000001");

        _catalog.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Product>
            {
                [KeyboardId] = Product.Create(
                    KeyboardId, "KB-001", "Mechanical Keyboard", Money.FromDecimal(49.99m, "USD")),
                [MouseId] = Product.Create(
                    MouseId, "MS-001", "Wireless Mouse", Money.FromDecimal(19.99m, "USD"))
            });
    }

    private OrderService CreateSut() => new(_orders, _catalog, _numbers, _clock);

    [Fact]
    public async Task Creating_an_order_prices_items_from_the_catalogue()
    {
        var result = await CreateSut().CreateAsync(
            new CreateOrderCommand(CustomerId, [new CreateOrderItemCommand(KeyboardId, 2)], null));

        result.WasCreated.ShouldBeTrue();
        result.Order.TotalAmount.ShouldBe(99.98m);
        result.Order.Items.Single().UnitPrice.ShouldBe(49.99m);
    }

    [Fact]
    public async Task Creating_an_order_persists_it_exactly_once()
    {
        await CreateSut().CreateAsync(
            new CreateOrderCommand(CustomerId, [new CreateOrderItemCommand(MouseId, 1)], null));

        _orders.Received(1).Add(Arg.Any<Order>());
        await _orders.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_order_with_no_items_is_rejected_before_touching_the_catalogue()
    {
        await Should.ThrowAsync<EmptyOrderException>(
            async () => await CreateSut().CreateAsync(
                new CreateOrderCommand(CustomerId, [], null)));

        await _catalog.DidNotReceive()
            .GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_unknown_product_is_rejected_and_nothing_is_persisted()
    {
        var unknown = Guid.CreateVersion7();

        await Should.ThrowAsync<ProductNotFoundException>(
            async () => await CreateSut().CreateAsync(
                new CreateOrderCommand(CustomerId, [new CreateOrderItemCommand(unknown, 1)], null)));

        _orders.DidNotReceive().Add(Arg.Any<Order>());
        await _orders.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_inactive_product_is_rejected()
    {
        var inactiveId = Guid.CreateVersion7();
        _catalog.GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, Product>
            {
                [inactiveId] = Product.Create(
                    inactiveId, "DC-001", "Discontinued", Money.FromDecimal(10m, "USD"), isActive: false)
            });

        await Should.ThrowAsync<ProductInactiveException>(
            async () => await CreateSut().CreateAsync(
                new CreateOrderCommand(CustomerId, [new CreateOrderItemCommand(inactiveId, 1)], null)));
    }

    [Fact]
    public async Task A_matching_idempotency_key_returns_the_original_without_creating_another()
    {
        var existing = BuildOrder(idempotencyKey: "key-1", quantity: 2);

        _orders.FindByIdempotencyKeyAsync(CustomerId, "key-1", Arg.Any<CancellationToken>())
            .Returns(existing);

        var result = await CreateSut().CreateAsync(
            new CreateOrderCommand(CustomerId, [new CreateOrderItemCommand(KeyboardId, 2)], "key-1"));

        result.WasCreated.ShouldBeFalse();
        result.Order.Id.ShouldBe(existing.Id);
        _orders.DidNotReceive().Add(Arg.Any<Order>());
    }

    [Fact]
    public async Task An_idempotency_key_reused_with_a_different_payload_is_rejected()
    {
        var existing = BuildOrder(idempotencyKey: "key-1", quantity: 2);

        _orders.FindByIdempotencyKeyAsync(CustomerId, "key-1", Arg.Any<CancellationToken>())
            .Returns(existing);

        await Should.ThrowAsync<IdempotencyKeyConflictException>(
            async () => await CreateSut().CreateAsync(
                new CreateOrderCommand(
                    CustomerId,
                    [new CreateOrderItemCommand(KeyboardId, 99)],
                    "key-1")));
    }

    [Fact]
    public async Task Reading_an_order_scopes_the_query_to_the_calling_customer()
    {
        // The security-critical assertion: the use case must never ask the repository
        // for an unrestricted read on behalf of a customer.
        _orders.FindAsync(Arg.Any<Guid>(), Arg.Any<OrderScope>(), Arg.Any<CancellationToken>())
            .Returns(BuildOrder());

        await CreateSut().GetAsync(Guid.CreateVersion7(), Actor.Customer(CustomerId));

        await _orders.Received(1).FindAsync(
            Arg.Any<Guid>(),
            Arg.Is<OrderScope>(scope => scope.CustomerId == CustomerId),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Reading_an_order_as_an_admin_is_unrestricted()
    {
        _orders.FindAsync(Arg.Any<Guid>(), Arg.Any<OrderScope>(), Arg.Any<CancellationToken>())
            .Returns(BuildOrder());

        await CreateSut().GetAsync(Guid.CreateVersion7(), Actor.Admin(Guid.CreateVersion7()));

        await _orders.Received(1).FindAsync(
            Arg.Any<Guid>(),
            Arg.Is<OrderScope>(scope => scope.IsUnrestricted),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_missing_order_surfaces_as_not_found()
    {
        _orders.FindAsync(Arg.Any<Guid>(), Arg.Any<OrderScope>(), Arg.Any<CancellationToken>())
            .Returns((Order?)null);

        await Should.ThrowAsync<OrderNotFoundException>(
            async () => await CreateSut().GetAsync(Guid.CreateVersion7(), Actor.Customer(CustomerId)));
    }

    [Fact]
    public async Task A_customer_cannot_widen_the_list_scope_with_a_customerId()
    {
        // FR-4.4: the parameter must be dropped, not passed through to the repository.
        var otherCustomer = Guid.CreateVersion7();
        _orders.ListAsync(Arg.Any<OrderQueryCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new OrderPage([], 0));

        await CreateSut().ListAsync(
            new ListOrdersQuery(otherCustomer, null),
            Actor.Customer(CustomerId));

        await _orders.Received(1).ListAsync(
            Arg.Is<OrderQueryCriteria>(criteria =>
                criteria.Scope.CustomerId == CustomerId && criteria.CustomerId == null),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_admin_may_narrow_the_list_to_one_customer()
    {
        var target = Guid.CreateVersion7();
        _orders.ListAsync(Arg.Any<OrderQueryCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new OrderPage([], 0));

        await CreateSut().ListAsync(
            new ListOrdersQuery(target, null),
            Actor.Admin(Guid.CreateVersion7()));

        await _orders.Received(1).ListAsync(
            Arg.Is<OrderQueryCriteria>(criteria =>
                criteria.Scope.IsUnrestricted && criteria.CustomerId == target),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(10_000, 100)]   // FR-4.6: clamped to the maximum
    [InlineData(0, 20)]         // falls back to the default
    [InlineData(-5, 20)]
    [InlineData(50, 50)]        // honoured
    public async Task Page_size_is_clamped_before_reaching_the_repository(int requested, int expected)
    {
        _orders.ListAsync(Arg.Any<OrderQueryCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new OrderPage([], 0));

        await CreateSut().ListAsync(
            new ListOrdersQuery(null, null, Page: 1, PageSize: requested),
            Actor.Customer(CustomerId));

        await _orders.Received(1).ListAsync(
            Arg.Is<OrderQueryCriteria>(criteria => criteria.PageSize == expected),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_page_number_below_one_is_normalised()
    {
        _orders.ListAsync(Arg.Any<OrderQueryCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new OrderPage([], 0));

        await CreateSut().ListAsync(
            new ListOrdersQuery(null, null, Page: -3),
            Actor.Customer(CustomerId));

        await _orders.Received(1).ListAsync(
            Arg.Is<OrderQueryCriteria>(criteria => criteria.Page == 1),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_same_status_transition_does_not_write()
    {
        // FR-3.5: an idempotent no-op must not issue a save.
        _orders.FindForUpdateAsync(Arg.Any<Guid>(), Arg.Any<OrderScope>(), Arg.Any<CancellationToken>())
            .Returns(BuildOrder());

        await CreateSut().TransitionAsync(
            Guid.CreateVersion7(),
            OrderStatus.Pending,
            Actor.Admin(Guid.CreateVersion7()),
            reason: null);

        await _orders.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_real_transition_writes_once()
    {
        _orders.FindForUpdateAsync(Arg.Any<Guid>(), Arg.Any<OrderScope>(), Arg.Any<CancellationToken>())
            .Returns(BuildOrder());

        var result = await CreateSut().TransitionAsync(
            Guid.CreateVersion7(),
            OrderStatus.Processing,
            Actor.Admin(Guid.CreateVersion7()),
            reason: null);

        result.Status.ShouldBe("PROCESSING");
        await _orders.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_illegal_transition_is_rejected_without_writing()
    {
        _orders.FindForUpdateAsync(Arg.Any<Guid>(), Arg.Any<OrderScope>(), Arg.Any<CancellationToken>())
            .Returns(BuildOrder());

        await Should.ThrowAsync<InvalidStatusTransitionException>(
            async () => await CreateSut().TransitionAsync(
                Guid.CreateVersion7(),
                OrderStatus.Delivered,
                Actor.Admin(Guid.CreateVersion7()),
                reason: null));

        await _orders.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancelling_uses_the_scoped_lookup_for_the_caller()
    {
        _orders.FindForUpdateAsync(Arg.Any<Guid>(), Arg.Any<OrderScope>(), Arg.Any<CancellationToken>())
            .Returns(BuildOrder());

        await CreateSut().CancelAsync(Guid.CreateVersion7(), Actor.Customer(CustomerId), reason: null);

        await _orders.Received(1).FindForUpdateAsync(
            Arg.Any<Guid>(),
            Arg.Is<OrderScope>(scope => scope.CustomerId == CustomerId),
            Arg.Any<CancellationToken>());
    }

    private static Order BuildOrder(string? idempotencyKey = null, int quantity = 1) =>
        Order.Create(
            Guid.CreateVersion7(),
            "ORD-2026-000001",
            CustomerId,
            [new OrderLine(
                KeyboardId,
                "Mechanical Keyboard",
                Money.FromDecimal(49.99m, "USD"),
                quantity)],
            "USD",
            Now,
            idempotencyKey);
}
