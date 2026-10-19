using ElevatorSystem.Application;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Api.Hosting;

/// <summary>
/// Composes the elevator core inside the API process. Phase 2 has no broker, so the control loop runs
/// here as a hosted service; phase 3 moves it to the engine behind RabbitMQ (ADR-0003).
/// The <see cref="IElevatorEventSink"/> it publishes to is registered by the real-time fan-out.
/// </summary>
internal static class ElevatorHostingExtensions
{
    /// <summary>Configuration section bound to <see cref="ElevatorControllerOptions"/>.</summary>
    public const string ElevatorSection = "Elevator";

    public static IHostApplicationBuilder AddElevator(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.TryAddSingleton(TimeProvider.System);
        services.AddOptions<ElevatorControllerOptions>().BindConfiguration(ElevatorSection).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ElevatorControllerOptions>, ElevatorControllerOptionsValidator>();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<ElevatorControllerOptions>>().Value);

        services.AddSingleton(Building.Default);
        services.AddSingleton<ISchedulingStrategy, FifoSchedulingStrategy>();
        services.AddSingleton(provider => new Elevator(
            provider.GetRequiredService<Building>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IElevatorDrive>(provider => new SimulatedElevatorDrive(
            provider.GetRequiredService<ElevatorControllerOptions>().TravelTimePerFloor,
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ElevatorController>();

        services.AddSingleton<ControlLoopStatus>();
        services.AddHostedService<ElevatorControlLoopService>();
        services.AddHealthChecks()
            .AddCheck<ControlLoopHealthCheck>("elevator-control-loop", tags: ["ready"]);

        return builder;
    }

    /// <summary>Reports options that <see cref="ElevatorControllerOptions.Validate"/> refuses as a start-up failure.</summary>
    private sealed class ElevatorControllerOptionsValidator : IValidateOptions<ElevatorControllerOptions>
    {
        public ValidateOptionsResult Validate(string? name, ElevatorControllerOptions options)
        {
            try
            {
                options.Validate();
                return ValidateOptionsResult.Success;
            }
            catch (ArgumentException exception)
            {
                return ValidateOptionsResult.Fail($"Configuration section '{ElevatorSection}' is invalid: {exception.Message}");
            }
        }
    }
}
