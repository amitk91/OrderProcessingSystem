using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OrderProcessing.Application.Abstractions;
using OrderProcessing.Infrastructure.Persistence;
using OrderProcessing.Infrastructure.Security;

namespace OrderProcessing.Integration.Tests.Infrastructure;

/// <summary>
/// Boots the API against an isolated SQLite database for integration tests
/// (specification section 12.3).
/// </summary>
/// <remarks>
/// Uses a real SQLite database rather than the EF Core InMemory provider. InMemory
/// enforces no unique constraints, foreign keys or check constraints, so tests for
/// idempotency uniqueness and referential integrity would pass there without
/// exercising anything.
///
/// Each factory instance owns a private database. A shared in-memory SQLite database
/// is named uniquely per factory and kept alive by a held-open connection, since
/// SQLite discards an in-memory database when its last connection closes.
///
/// The background promotion job is disabled by default: tests that are not about the
/// scheduler must not race it, and tests that are about it invoke the service directly
/// so they control timing rather than waiting on a timer.
/// </remarks>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    private readonly string _databaseName = $"orders-test-{Guid.CreateVersion7():N}";
    private SqliteConnection? _keepAliveConnection;

    private string ConnectionString =>
        $"Data Source={_databaseName};Mode=Memory;Cache=Shared";

    public async Task InitializeAsync()
    {
        // Holding one connection open keeps the shared in-memory database alive for
        // the lifetime of this factory.
        _keepAliveConnection = new SqliteConnection(ConnectionString);
        await _keepAliveConnection.OpenAsync();

        using var scope = Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();

        // Migrate rather than EnsureCreated, so tests run against the schema that ships.
        await dbContext.Database.MigrateAsync();

        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await DatabaseSeeder.SeedAsync(dbContext, timeProvider);
    }

    public new async Task DisposeAsync()
    {
        if (_keepAliveConnection is not null)
        {
            await _keepAliveConnection.DisposeAsync();
        }

        await base.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Testing");

        builder.ConfigureAppConfiguration((_, configuration) =>
            configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = ConnectionString,
                ["OrderPromotion:Enabled"] = "false",
                ["Jwt:SigningKey"] = "integration-test-signing-key-0123456789abcdef",
                ["Jwt:Issuer"] = "order-processing",
                ["Jwt:Audience"] = "order-processing-api"
            }));

        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Warning));
    }

    public HttpClient CreateClientFor(Guid userId, string role)
    {
        var client = CreateClient();
        var issuer = Services.GetRequiredService<JwtTokenIssuer>();
        var token = issuer.IssueToken(userId, role, $"{userId:N}@example.com");

        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        return client;
    }

    public HttpClient CreateAliceClient() =>
        CreateClientFor(DatabaseSeeder.AliceId, AuthConstants.CustomerRole);

    public HttpClient CreateBobClient() =>
        CreateClientFor(DatabaseSeeder.BobId, AuthConstants.CustomerRole);

    public HttpClient CreateAdminClient() =>
        CreateClientFor(DatabaseSeeder.AdminId, AuthConstants.AdminRole);

    public AsyncServiceScope CreateAsyncScope() => Services.CreateAsyncScope();
}
