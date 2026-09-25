using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace OrderProcessing.Infrastructure.Scheduling;

/// <summary>
/// Runs <see cref="OrderPromotionService"/> on a fixed interval
/// (FR-6.1, specification section 9).
/// </summary>
/// <remarks>
/// Uses <see cref="TimeProvider.CreateTimer"/> rather than <c>DateTime.UtcNow</c> and a
/// real <c>PeriodicTimer</c>, so tests can advance a fake clock and verify a five-minute
/// schedule in milliseconds with no <c>Thread.Sleep</c> (specification section 9.2).
///
/// Runs never overlap: the timer is restarted only after the previous run finishes, so a
/// run that outlives its interval delays the next tick instead of racing itself.
/// </remarks>
internal sealed class OrderPromotionBackgroundService(
    IServiceScopeFactory scopeFactory,
    IOptions<OrderPromotionOptions> options,
    TimeProvider timeProvider,
    ILogger<OrderPromotionBackgroundService> logger) : BackgroundService
{
    private readonly OrderPromotionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            PromotionLog.JobDisabled(logger);
            return;
        }

        PromotionLog.JobStarted(logger, _options.Interval, _options.BatchSize);

        try
        {
            await Task.Delay(_options.InitialDelay, timeProvider, stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                await RunOnceAsync(stoppingToken);
                await Task.Delay(_options.Interval, timeProvider, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // FR-6.9: expected during shutdown, not an error.
            PromotionLog.JobStopping(logger);
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var runId = Guid.CreateVersion7();
        var startedAt = timeProvider.GetTimestamp();

        // A scope per run: the DbContext is scoped, and a single long-lived context
        // would accumulate tracked entities for the lifetime of the process.
        using var scope = scopeFactory.CreateScope();
        var promotionService = scope.ServiceProvider.GetRequiredService<OrderPromotionService>();

        try
        {
            var result = await promotionService.PromotePendingOrdersAsync(
                _options.BatchSize,
                _options.MaxBatchesPerRun,
                cancellationToken);

            var elapsed = timeProvider.GetElapsedTime(startedAt);

            if (result.Promoted > 0 || result.Failed > 0)
            {
                PromotionLog.RunCompleted(
                    logger,
                    runId,
                    result.Claimed,
                    result.Promoted,
                    result.Failed,
                    elapsed.TotalMilliseconds);
            }
            else
            {
                PromotionLog.RunFoundNothing(logger, runId, elapsed.TotalMilliseconds);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // A failed run must not kill the job; the next tick retries.
            // Logged at Error so a job silently failing forever is visible.
            PromotionLog.RunFailed(logger, runId, ex);
        }
    }
}
