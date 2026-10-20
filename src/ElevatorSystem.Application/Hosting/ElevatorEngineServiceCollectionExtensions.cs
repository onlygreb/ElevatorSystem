using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Application.Hosting;

/// <summary>Registers the engine the same way in every host: the engine service, and the API in single-process mode.</summary>
public static class ElevatorEngineServiceCollectionExtensions
{
    /// <summary>Configuration section bound to <see cref="ElevatorControllerOptions"/>.</summary>
    public const string ElevatorSection = "Elevator";

    /// <summary>Configuration section bound to <see cref="OccupancyOptions"/>.</summary>
    public const string OccupancySection = "Occupancy";

    /// <summary>
    /// Binds <see cref="ElevatorControllerOptions"/> from <see cref="ElevatorSection"/>, validated at start-up,
    /// and registers the <see cref="Building"/>, the FIFO strategy and <see cref="TimeProvider.System"/>
    /// unless the host chose others. Every host that describes the elevator needs these.
    /// </summary>
    public static IServiceCollection AddElevatorOptions(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(Building.Default);
        services.TryAddSingleton<ISchedulingStrategy, FifoSchedulingStrategy>();
        services.AddOptions<ElevatorControllerOptions>().BindConfiguration(ElevatorSection).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ElevatorControllerOptions>, ControllerOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<ElevatorControllerOptions>>().Value);
        return services;
    }

    /// <summary>
    /// Registers <see cref="ElevatorEngine"/> and the hosted service that runs it, with options from the
    /// <see cref="ElevatorSection"/>, <see cref="ElevatorEngineOptions.SectionName"/> and <see cref="OccupancySection"/> sections and the
    /// simulated drive. The host registers the messaging and persistence ports.
    /// </summary>
    public static IServiceCollection AddElevatorEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElevatorOptions();
        services.AddOptions<ElevatorEngineOptions>().BindConfiguration(ElevatorEngineOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ElevatorEngineOptions>, EngineOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<ElevatorEngineOptions>>().Value);
        services.AddOptions<OccupancyOptions>().BindConfiguration(OccupancySection).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<OccupancyOptions>, OccupancyOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<OccupancyOptions>>().Value);

        services.TryAddSingleton(provider =>
        {
            var controllerOptions = provider.GetRequiredService<ElevatorControllerOptions>();
            var timeProvider = provider.GetRequiredService<TimeProvider>();
            return new ElevatorEngine(
                provider.GetRequiredService<ILeaderElector>(),
                provider.GetRequiredService<IElevatorStateStore>(),
                provider.GetRequiredService<IProcessedCommandStore>(),
                provider.GetRequiredService<IElevatorCommandConsumer>(),
                provider.GetRequiredService<IElevatorUpdatePublisher>(),
                provider.GetRequiredService<Building>(),
                provider.GetRequiredService<ISchedulingStrategy>(),
                () => new SimulatedElevatorDrive(controllerOptions.TravelTimePerFloor, timeProvider),
                controllerOptions,
                provider.GetRequiredService<ElevatorEngineOptions>(),
                provider.GetRequiredService<OccupancyOptions>(),
                timeProvider,
                provider.GetRequiredService<ILoggerFactory>());
        });
        services.AddHostedService<ElevatorEngineService>();
        return services;
    }

    /// <summary>Reports options that <see cref="ElevatorControllerOptions.Validate"/> refuses as a start-up failure.</summary>
    private sealed class ControllerOptionsValidator : IValidateOptions<ElevatorControllerOptions>
    {
        public ValidateOptionsResult Validate(string? name, ElevatorControllerOptions options) =>
            Check(ElevatorSection, options.Validate);
    }

    /// <summary>Reports options that <see cref="ElevatorEngineOptions.Validate"/> refuses as a start-up failure.</summary>
    private sealed class EngineOptionsValidator : IValidateOptions<ElevatorEngineOptions>
    {
        public ValidateOptionsResult Validate(string? name, ElevatorEngineOptions options) =>
            Check(ElevatorEngineOptions.SectionName, options.Validate);
    }

    /// <summary>Reports options that <see cref="OccupancyOptions.Validate"/> refuses as a start-up failure.</summary>
    private sealed class OccupancyOptionsValidator : IValidateOptions<OccupancyOptions>
    {
        public ValidateOptionsResult Validate(string? name, OccupancyOptions options) =>
            Check(OccupancySection, options.Validate);
    }

    private static ValidateOptionsResult Check(string section, Action validate)
    {
        try
        {
            validate();
            return ValidateOptionsResult.Success;
        }
        catch (ArgumentException exception)
        {
            return ValidateOptionsResult.Fail($"Configuration section '{section}' is invalid: {exception.Message}");
        }
    }
}
