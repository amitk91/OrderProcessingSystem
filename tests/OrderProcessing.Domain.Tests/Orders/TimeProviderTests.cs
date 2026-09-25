using Microsoft.Extensions.Time.Testing;
using OrderProcessing.Domain.Common;
using OrderProcessing.Domain.Orders;
using Shouldly;

namespace OrderProcessing.Domain.Tests.Orders;

/// <summary>
/// Demonstrates that time-dependent behaviour is deterministically testable because
/// the system takes its clock from <see cref="TimeProvider"/> rather than
/// <c>DateTime.UtcNow</c> (specification section 9.2).
/// </summary>
/// <remarks>
/// The practical payoff: a five-minute schedule is verified in microseconds, with no
/// <c>Thread.Sleep</c> and no flakiness from a slow machine.
/// </remarks>
public sealed class TimeProviderTests
{
    [Fact]
    public void Timestamps_come_from_the_injected_clock()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));

        var order = Order.Create(
            Guid.CreateVersion7(),
            "ORD-2026-000001",
            Guid.CreateVersion7(),
            [new OrderLine(Guid.CreateVersion7(), "Widget", Money.FromDecimal(10m, "USD"), 1)],
            "USD",
            clock.GetUtcNow());

        order.CreatedAt.ShouldBe(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void Advancing_the_clock_five_minutes_takes_no_real_time()
    {
        var start = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);

        var order = Order.Create(
            Guid.CreateVersion7(),
            "ORD-2026-000002",
            Guid.CreateVersion7(),
            [new OrderLine(Guid.CreateVersion7(), "Widget", Money.FromDecimal(10m, "USD"), 1)],
            "USD",
            clock.GetUtcNow());

        // The promotion interval from the brief, simulated instantly.
        clock.Advance(TimeSpan.FromMinutes(5));
        order.PromoteToProcessing(clock.GetUtcNow());

        order.Status.ShouldBe(OrderStatus.Processing);
        order.UpdatedAt.ShouldBe(start.AddMinutes(5));
        (order.UpdatedAt - order.CreatedAt).ShouldBe(TimeSpan.FromMinutes(5));
    }

    [Fact]
    public async Task A_periodic_schedule_fires_on_each_interval_without_waiting()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero));
        var ticks = 0;

        using var timer = clock.CreateTimer(
            _ => Interlocked.Increment(ref ticks),
            state: null,
            dueTime: TimeSpan.FromMinutes(5),
            period: TimeSpan.FromMinutes(5));

        clock.Advance(TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(5));
        clock.Advance(TimeSpan.FromMinutes(5));

        await Task.Yield();

        // Fifteen simulated minutes, three runs, no elapsed wall-clock time.
        Volatile.Read(ref ticks).ShouldBe(3);
    }

    [Fact]
    public void The_audit_trail_records_the_time_each_transition_occurred()
    {
        var start = new DateTimeOffset(2026, 3, 1, 9, 0, 0, TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        var admin = Actor.Admin(Guid.CreateVersion7());

        var order = Order.Create(
            Guid.CreateVersion7(),
            "ORD-2026-000003",
            Guid.CreateVersion7(),
            [new OrderLine(Guid.CreateVersion7(), "Widget", Money.FromDecimal(10m, "USD"), 1)],
            "USD",
            clock.GetUtcNow());

        clock.Advance(TimeSpan.FromMinutes(5));
        order.TransitionTo(OrderStatus.Processing, admin, clock.GetUtcNow());

        clock.Advance(TimeSpan.FromHours(2));
        order.TransitionTo(OrderStatus.Shipped, admin, clock.GetUtcNow());

        order.StatusHistory.Select(entry => entry.ChangedAt).ShouldBe(
        [
            start,
            start.AddMinutes(5),
            start.AddMinutes(5).AddHours(2)
        ]);
    }
}
