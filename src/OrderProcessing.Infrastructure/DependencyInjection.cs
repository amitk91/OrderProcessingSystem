using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Infrastructure.Orders;
using OrderProcessing.Infrastructure.Persistence;
using OrderProcessing.Infrastructure.Scheduling;
using OrderProcessing.Infrastructure.Security;

namespace OrderProcessing.Infrastructure;

/// <summary>
/// Composition root for the infrastructure layer.
/// </summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddPersistence(configuration);
        services.AddScheduling(configuration);
        services.AddSecurity(configuration);

        services.AddScoped<OrderService>();
        services.TryAddSingletonTimeProvider();

        return services;
    }

    private static IServiceCollection AddPersistence(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("Default")
            ?? "Data Source=orders.db";

        services.AddDbContext<OrderProcessingDbContext>(options =>
        {
            options.UseSqlite(connectionString, sqlite =>
                sqlite.MigrationsAssembly(typeof(OrderProcessingDbContext).Assembly.FullName));
        });

        services.AddScoped<IOrderNumberGenerator, OrderNumberGenerator>();

        return services;
    }

    private static IServiceCollection AddScheduling(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<OrderPromotionOptions>()
            .Bind(configuration.GetSection(OrderPromotionOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<IPendingOrderClaimer, SqlitePendingOrderClaimer>();
        services.AddScoped<OrderPromotionService>();
        services.AddHostedService<OrderPromotionBackgroundService>();

        return services;
    }

    private static IServiceCollection AddSecurity(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddSingleton<JwtTokenIssuer>();

        services
            .AddAuthentication("Bearer")
            .AddJwtBearer("Bearer");

        // Validation parameters are configured from IOptions rather than read eagerly
        // from IConfiguration here. Reading eagerly would capture whatever the config
        // held at registration time, so any provider layered in afterwards (a test
        // harness, a secret store) would leave the token issuer and the token
        // validator disagreeing about the signing key — tokens would be issued and
        // then rejected, with only a bare 401 to explain it.
        services.AddOptions<JwtBearerOptions>("Bearer")
            .Configure<IOptions<JwtOptions>>((bearerOptions, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;

                bearerOptions.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(
                        Encoding.UTF8.GetBytes(jwt.SigningKey)),

                    // No tolerance for expiry drift; the default five minutes is
                    // surprising behaviour in a system that cares about timing.
                    ClockSkew = TimeSpan.Zero
                };
            });

        services.AddAuthorizationBuilder()
            .AddPolicy(AuthConstants.AdminOnlyPolicy, policy =>
                policy.RequireRole(AuthConstants.AdminRole));

        return services;
    }

    private static void TryAddSingletonTimeProvider(this IServiceCollection services)
    {
        // Registered here rather than in the API so tests that build only the
        // infrastructure graph still get a clock, and can substitute a fake one.
        if (services.All(descriptor => descriptor.ServiceType != typeof(TimeProvider)))
        {
            services.AddSingleton(TimeProvider.System);
        }
    }
}
