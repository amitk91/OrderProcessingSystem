using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using OrderProcessing.Api.Middleware;
using OrderProcessing.Infrastructure;
using OrderProcessing.Infrastructure.Persistence;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// Structured JSON logs with a correlation id per request (specification section 10.4).
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

builder.Services.AddInfrastructure(builder.Configuration);

builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();

// RFC 7807 for every error, including ones the framework produces (section 11.3).
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["correlationId"] = context.HttpContext.TraceIdentifier);

builder.Services.AddExceptionHandler<ProblemDetailsExceptionHandler>();

builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Order Processing API",
        Version = "v1",
        Description = "E-commerce order processing system. See docs/SPECIFICATION.md."
    });

    // Lets a reviewer paste a token from /api/v1/dev/token and call secured endpoints
    // directly from the Swagger UI.
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Paste the token from POST /api/v1/dev/token."
    });

    options.AddSecurityRequirement(_ => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("Bearer")] = []
    });
});

builder.Services.AddHealthChecks()
    .AddDbContextCheck<OrderProcessingDbContext>("database", tags: ["ready"]);

var app = builder.Build();

await InitialiseDatabaseAsync(app);

app.UseExceptionHandler();
app.UseStatusCodePages();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "Order Processing API v1");
        options.DocumentTitle = "Order Processing API";
    });
}

app.UseSerilogRequestLogging();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready")
});

await app.RunAsync();

/// <summary>
/// Applies migrations and seeds development data, so the API is usable immediately
/// after <c>dotnet run</c> with no external setup (specification section 13.1).
/// </summary>
static async Task InitialiseDatabaseAsync(WebApplication app)
{
    // Integration tests configure their own database, so skip this there.
    if (app.Environment.IsEnvironment("Testing"))
    {
        return;
    }

    using var scope = app.Services.CreateScope();
    var dbContext = scope.ServiceProvider.GetRequiredService<OrderProcessingDbContext>();

    await dbContext.Database.MigrateAsync();

    if (app.Environment.IsDevelopment())
    {
        var timeProvider = scope.ServiceProvider.GetRequiredService<TimeProvider>();
        await DatabaseSeeder.SeedAsync(dbContext, timeProvider);
    }
}

/// <summary>
/// Exposed so <c>WebApplicationFactory&lt;Program&gt;</c> can bootstrap the API in
/// integration tests (specification section 12.1).
/// </summary>
public partial class Program;
