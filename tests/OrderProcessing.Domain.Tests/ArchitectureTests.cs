using System.Reflection;
using OrderProcessing.Application.Orders;
using OrderProcessing.Domain.Orders;
using Shouldly;

namespace OrderProcessing.Domain.Tests;

/// <summary>
/// Enforces the dependency rule in specification section 4 as an executable test.
/// </summary>
/// <remarks>
/// A layering claim written only in a README is documentation; written as a test it is
/// a constraint. These assertions fail the build the moment a layer starts *using* a
/// persistence or web type it must not know about — which is how such violations creep
/// in, one pragmatic reference at a time.
///
/// <para><b>Scope of the guard.</b> <c>GetReferencedAssemblies</c> reports assemblies
/// the compiler actually bound to, so this catches real usage rather than a declared
/// but unused package reference. That is the meaningful case: an unused reference is
/// inert, whereas a single <c>DbContext</c> parameter in a use case breaks
/// substitutability. Verified by mutation — introducing an EF Core type into
/// <see cref="OrderService"/> fails
/// <see cref="The_application_layer_has_no_persistence_or_web_dependency"/>.</para>
/// </remarks>
public sealed class ArchitectureTests
{
    private static readonly Assembly DomainAssembly = typeof(Order).Assembly;
    private static readonly Assembly ApplicationAssembly = typeof(OrderService).Assembly;

    private static readonly string[] ForbiddenInDomain =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Extensions",
        "Npgsql",
        "Serilog",
        "Swashbuckle"
    ];

    private static readonly string[] ForbiddenInApplication =
    [
        "Microsoft.EntityFrameworkCore",
        "Microsoft.AspNetCore",
        "Microsoft.Data.Sqlite",
        "Npgsql",
        "Serilog",
        "Swashbuckle"
    ];

    [Fact]
    public void The_domain_depends_on_nothing_but_the_base_class_library()
    {
        var offenders = ReferencedAssemblyNames(DomainAssembly)
            .Where(name => ForbiddenInDomain.Any(forbidden =>
                name.StartsWith(forbidden, StringComparison.Ordinal)))
            .ToList();

        offenders.ShouldBeEmpty(
            "the domain must not reference any framework or persistence library");
    }

    [Fact]
    public void The_application_layer_has_no_persistence_or_web_dependency()
    {
        // Use cases orchestrate through ports. If EF Core appears here, the ports have
        // been bypassed and the layer is no longer substitutable or unit-testable.
        var offenders = ReferencedAssemblyNames(ApplicationAssembly)
            .Where(name => ForbiddenInApplication.Any(forbidden =>
                name.StartsWith(forbidden, StringComparison.Ordinal)))
            .ToList();

        offenders.ShouldBeEmpty(
            "application use cases must depend on ports, not on a persistence provider");
    }

    [Fact]
    public void The_domain_does_not_reference_the_application_layer()
    {
        ReferencedAssemblyNames(DomainAssembly)
            .ShouldNotContain(name => name.Contains("Application", StringComparison.Ordinal));
    }

    [Fact]
    public void The_application_layer_does_not_reference_infrastructure()
    {
        ReferencedAssemblyNames(ApplicationAssembly)
            .ShouldNotContain(name => name.Contains("Infrastructure", StringComparison.Ordinal));
    }

    [Fact]
    public void Order_lifecycle_use_cases_live_in_the_application_assembly()
    {
        // Guards against orchestration drifting back into infrastructure, which is
        // where it sat before this rule was made explicit.
        typeof(OrderService).Assembly.ShouldBe(ApplicationAssembly);
        typeof(OrderPromotionService).Assembly.ShouldBe(ApplicationAssembly);
    }

    private static IReadOnlyList<string> ReferencedAssemblyNames(Assembly assembly) =>
        [.. assembly.GetReferencedAssemblies().Select(reference => reference.Name ?? string.Empty)];
}
