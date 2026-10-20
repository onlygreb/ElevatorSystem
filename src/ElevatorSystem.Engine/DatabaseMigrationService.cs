using ElevatorSystem.Infrastructure.Persistence;

namespace ElevatorSystem.Engine;

/// <summary>
/// Applies pending migrations while the host starts, before any service that uses the database: hosted
/// services start one after another in registration order, and this one is registered first (ADR-0019 §7).
/// While the database is unreachable it retries, so the engine stays <c>starting</c> (not ready) instead of
/// crashing in a loop; health endpoints already answer, because the web server starts before it.
/// </summary>
internal sealed partial class DatabaseMigrationService(
    ElevatorDatabaseMigrator migrator, TimeProvider timeProvider, ILogger<DatabaseMigrationService> logger) : IHostedService
{
    public static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(2);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            try
            {
                await migrator.MigrateAsync(cancellationToken);
                LogMigrated(logger);
                return;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                LogMigrationFailed(logger, exception, RetryInterval);
            }

            await Task.Delay(RetryInterval, timeProvider, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    // Engine host log event IDs: 2100-2199. No connection strings or personal data.
    [LoggerMessage(2100, LogLevel.Information, "The elevator database schema is up to date")]
    private static partial void LogMigrated(ILogger logger);

    [LoggerMessage(2101, LogLevel.Error, "Migrating the elevator database failed; retrying in {RetryInterval}")]
    private static partial void LogMigrationFailed(ILogger logger, Exception exception, TimeSpan retryInterval);
}
