using Microsoft.Extensions.DependencyInjection;
using OrderProcessing.Application.Orders;

namespace OrderProcessing.Application;

/// <summary>
/// Registers the application layer's use cases.
/// </summary>
/// <remarks>
/// Separate from the infrastructure registration so the composition root wires the two
/// layers independently, and so it is visible at a glance that use cases have no
/// infrastructure dependencies of their own — they need only the ports, which
/// infrastructure supplies.
/// </remarks>
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<OrderService>();
        services.AddScoped<OrderPromotionService>();

        return services;
    }
}
