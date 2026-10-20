using Microsoft.Extensions.Hosting;

namespace ElevatorSystem.Application.Hosting;

/// <summary>Runs <see cref="ElevatorEngine.RunAsync"/> for the lifetime of the host.</summary>
/// <remarks>
/// The engine contains its own failures, so it only ends on shutdown. If it ever throws, the host's default
/// <c>BackgroundServiceExceptionBehavior.StopHost</c> stops the process and the orchestrator restarts
/// it cleanly (ADR-0007). A host that must prepare something first (the engine host migrates the database)
/// registers a hosted service ahead of this one: hosted services start in registration order.
/// </remarks>
public sealed class ElevatorEngineService(ElevatorEngine engine) : BackgroundService
{
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => engine.RunAsync(stoppingToken);
}
