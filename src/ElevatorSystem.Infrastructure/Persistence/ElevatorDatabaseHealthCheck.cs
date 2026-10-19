using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>
/// Reports whether the elevator database answers. Registered with a <c>Degraded</c> failure status:
/// the car keeps operating without its database (ADR-0007), but recovery and failover do not.
/// </summary>
public sealed class ElevatorDatabaseHealthCheck(ElevatorDatabase database) : IHealthCheck
{
    public const string Name = "elevator-database";

    private readonly ElevatorDatabase _database = database ?? throw new ArgumentNullException(nameof(database));

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        try
        {
            await using var command = _database.DataSource.CreateCommand("SELECT 1");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy("The elevator database answers.");
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new HealthCheckResult(context.Registration.FailureStatus, "The elevator database does not answer.", exception);
        }
    }
}
