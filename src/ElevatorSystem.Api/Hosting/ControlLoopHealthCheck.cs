using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ElevatorSystem.Api.Hosting;

/// <summary>Readiness: the API can only serve passengers while the elevator's control loop is running.</summary>
internal sealed class ControlLoopHealthCheck(ControlLoopStatus status) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(status.IsRunning
            ? HealthCheckResult.Healthy("The elevator control loop is running.")
            : HealthCheckResult.Unhealthy("The elevator control loop is not running."));
}
