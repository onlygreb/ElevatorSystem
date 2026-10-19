using ElevatorSystem.Application.Ports;
using Microsoft.Extensions.Hosting;

namespace ElevatorSystem.Infrastructure.Messaging;

/// <summary>
/// Connects and declares the topology when the host starts, retrying until the broker is reachable. It never blocks
/// or fails the start: a service without its broker runs degraded (ADR-0007) and reports so through its health check.
/// </summary>
public sealed class RabbitMqConnectionStarter(RabbitMqConnection connection) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await connection.GetConnectionAsync(stoppingToken);
                return;
            }
            catch (MessagingUnavailableException)
            {
                // Already logged by the connection; try again after the recovery interval.
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }

            try
            {
                await Task.Delay(connection.Options.RecoveryInterval, connection.TimeProvider, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
