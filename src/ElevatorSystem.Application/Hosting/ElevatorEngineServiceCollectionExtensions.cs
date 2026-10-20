using ElevatorSystem.Application.Passengers;
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

    /// <summary>Configuration section bound to <see cref="PassengerOptions"/>.</summary>
    public const string PassengersSection = PassengerOptions.SectionName;

    /// <summary>Configuration section bound to <see cref="SimulationOptions"/> (the demo scenarios).</summary>
    public const string SimulationSection = SimulationOptions.SectionName;

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
    /// <see cref="ElevatorSection"/>, <see cref="ElevatorEngineOptions.SectionName"/>, <see cref="PassengersSection"/> and <see cref="SimulationSection"/> sections and the
    /// simulated drive. The host registers the messaging and persistence ports.
    /// </summary>
    public static IServiceCollection AddElevatorEngine(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddElevatorOptions();
        services.AddOptions<ElevatorEngineOptions>().BindConfiguration(ElevatorEngineOptions.SectionName).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<ElevatorEngineOptions>, EngineOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<ElevatorEngineOptions>>().Value);
        services.AddOptions<PassengerOptions>().BindConfiguration(PassengersSection).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<PassengerOptions>, PassengerOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<PassengerOptions>>().Value);
        services.AddOptions<SimulationOptions>().BindConfiguration(SimulationSection).ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<SimulationOptions>, SimulationOptionsValidator>());
        services.TryAddSingleton(provider => provider.GetRequiredService<IOptions<SimulationOptions>>().Value);

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
                provider.GetRequiredService<PassengerOptions>(),
                provider.GetRequiredService<SimulationOptions>(),
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

    /// <summary>Reports options that <see cref="PassengerOptions.Validate"/> refuses as a start-up failure.</summary>
    private sealed class PassengerOptionsValidator : IValidateOptions<PassengerOptions>
    {
        public ValidateOptionsResult Validate(string? name, PassengerOptions options) =>
            Check(PassengersSection, options.Validate);
    }

    /// <summary>Reports options that <see cref="SimulationOptions.Validate"/> refuses as a start-up failure.</summary>
    private sealed class SimulationOptionsValidator : IValidateOptions<SimulationOptions>
    {
        public ValidateOptionsResult Validate(string? name, SimulationOptions options) =>
            Check(SimulationSection, options.Validate);
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
