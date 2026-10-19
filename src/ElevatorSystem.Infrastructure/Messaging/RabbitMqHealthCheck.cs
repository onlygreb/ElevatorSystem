using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// Readiness of the broker connection. Registered with failure status <see cref="HealthStatus.Degraded"/>: without the
/// broker the service keeps running (the car keeps operating, ADR-0007) but cannot accept or deliver commands.
/// </summary>
public sealed class RabbitMqHealthCheck(RabbitMqConnection connection) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        return Task.FromResult(connection.IsConnected
            ? HealthCheckResult.Healthy("Connected to the message broker.")
            : new HealthCheckResult(context.Registration.FailureStatus, "Not connected to the message broker; reconnecting."));
    }
}
