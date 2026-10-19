using ElevatorSystem.Application;

namespace ElevatorSystem.Api.Hosting;

/// <summary>Runs <see cref="ElevatorController.ProcessRequests"/> for the lifetime of the host.</summary>
/// <remarks>
/// The loop contains its own failures (ADR-0007), so it only ends on shutdown. If it ever throws, the
/// host's default <see cref="BackgroundServiceExceptionBehavior.StopHost"/> stops the process so the
/// orchestrator restarts it cleanly, and readiness reports the loop as stopped in the meantime.
/// </remarks>
internal sealed class ElevatorControlLoopService(ElevatorController controller, ControlLoopStatus status) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        status.IsRunning = true;
        try
        {
            await controller.ProcessRequests(stoppingToken);
        }
        finally
        {
            status.IsRunning = false;
        }
    }
}

/// <summary>Whether the control loop is running, shared between the loop's host and its readiness check.</summary>
internal sealed class ControlLoopStatus
{
    private volatile bool _isRunning;

    public bool IsRunning
    {
        get => _isRunning;
        set => _isRunning = value;
    }
}
