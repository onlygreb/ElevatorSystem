using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Purges expired processed command IDs at start-up and then on every
/// <see cref="PersistenceOptions.ProcessedCommandPurgeInterval"/> (docs/lgpd/retention.md). The purge
/// is idempotent and safe to run from every instance, so it needs no leadership. A failed run is
/// logged and retried at the next interval.
/// </summary>
public sealed class ProcessedCommandRetentionService(
    PostgresProcessedCommandStore store,
    IOptions<PersistenceOptions> options,
    TimeProvider timeProvider,
    ILogger<ProcessedCommandRetentionService>? logger = null) : BackgroundService
{
    private readonly PostgresProcessedCommandStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly PersistenceOptions _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
    private readonly TimeProvider _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    private readonly ILogger _logger = logger ?? NullLogger<ProcessedCommandRetentionService>.Instance;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var purged = await _store.PurgeExpiredAsync(stoppingToken);
                PersistenceLog.ProcessedCommandsPurged(_logger, purged, _options.ProcessedCommandRetention);
            }
            catch (Exception exception)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    return;
                }

                PersistenceLog.ProcessedCommandPurgeFailed(_logger, exception, _options.ProcessedCommandPurgeInterval);
            }

            try
            {
                await Task.Delay(_options.ProcessedCommandPurgeInterval, _timeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
