using System.Net;
using OrderProcessing.Api.Middleware;
using OrderProcessing.Application.Orders;
using OrderProcessing.Domain.Orders;
using Shouldly;

namespace OrderProcessing.Integration.Tests;

/// <summary>
/// Pins the exception-to-status-code mapping (specification section 11.4).
/// </summary>
/// <remarks>
/// C# switch arms are evaluated in order, so a general arm placed above a specific one
/// silently shadows it. That is not a hypothetical: <see cref="ConcurrencyConflictException"/>
/// derives from <c>DomainException</c>, and while the general arm sat first it was
/// answering 400 instead of 409 with nothing to indicate anything was wrong.
///
/// These assertions run against the mapping directly rather than through HTTP, so every
/// arm is covered including the ones that are hard to provoke over the wire.
/// </remarks>
public sealed class ProblemDetailsMappingTests
{
    public static TheoryData<Exception, HttpStatusCode, string> Mappings() => new()
    {
        { new OrderNotFoundException(Guid.CreateVersion7()), HttpStatusCode.NotFound, "order-not-found" },
        { new ProductNotFoundException(Guid.CreateVersion7()), HttpStatusCode.BadRequest, "product-not-found" },
        { new ProductInactiveException(Guid.CreateVersion7(), "Widget"), HttpStatusCode.UnprocessableEntity, "product-inactive" },
        { new IdempotencyKeyConflictException("key"), HttpStatusCode.UnprocessableEntity, "idempotency-key-conflict" },
        { new EmptyOrderException(), HttpStatusCode.BadRequest, "empty-order" },
        { new CancellationReasonRequiredException(), HttpStatusCode.BadRequest, "cancellation-reason-required" },
        {
            new InvalidStatusTransitionException(
                OrderStatus.Pending, OrderStatus.Delivered, ActorType.Admin, []),
            HttpStatusCode.Conflict,
            "invalid-status-transition"
        },
        {
            new ConcurrencyConflictException(Guid.CreateVersion7(), new InvalidOperationException()),
            HttpStatusCode.Conflict,
            "concurrency-conflict"
        },
        {
            new DuplicateIdempotencyKeyException("key", new InvalidOperationException()),
            HttpStatusCode.Conflict,
            "write-conflict"
        },
        {
            new DuplicateOrderNumberException("ORD-1", new InvalidOperationException()),
            HttpStatusCode.Conflict,
            "write-conflict"
        },
        { new InvalidOperationException("boom"), HttpStatusCode.InternalServerError, "internal-error" }
    };

    [Theory]
    [MemberData(nameof(Mappings))]
    public void Exceptions_map_to_the_specified_status_and_error_code(
        Exception exception,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        var (status, _, errorCode) = ProblemDetailsMapper.Map(exception);

        status.ShouldBe((int)expectedStatus);
        errorCode.ShouldBe(expectedCode);
    }

    [Fact]
    public void A_concurrency_conflict_is_not_swallowed_by_the_general_domain_arm()
    {
        // The specific regression this file exists to prevent.
        var (status, _, _) = ProblemDetailsMapper.Map(
            new ConcurrencyConflictException(Guid.CreateVersion7(), new InvalidOperationException()));

        status.ShouldBe((int)HttpStatusCode.Conflict);
        status.ShouldNotBe((int)HttpStatusCode.BadRequest);
    }

    [Fact]
    public void An_unrecognised_exception_never_leaks_internal_detail()
    {
        var (status, title, code) = ProblemDetailsMapper.Map(
            new InvalidOperationException("connection string: Server=secret;Password=hunter2"));

        status.ShouldBe((int)HttpStatusCode.InternalServerError);
        code.ShouldBe("internal-error");
        title.ShouldNotContain("hunter2");
    }
}
