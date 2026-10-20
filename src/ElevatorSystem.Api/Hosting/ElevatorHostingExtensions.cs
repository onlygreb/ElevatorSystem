using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Application.Diagnostics;
using ElevatorSystem.Application.Hosting;
using ElevatorSystem.Application.InMemory.Messaging;
using ElevatorSystem.Application.InMemory.Persistence;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Api.Hosting;

/// <summary>Where the elevator engine runs, relative to the API (ADR-0021).</summary>
public enum HostingMode
{
    /// <summary>
    /// The engine runs in its own processes behind RabbitMQ and PostgreSQL; the API publishes commands and
    /// serves the state its update subscription reports. The default, and what Aspire runs.
    /// </summary>
    Distributed,

    /// <summary>
    /// The engine runs inside the API process over the in-memory bus and stores: the same code paths with
    /// no infrastructure, for development and the API tests. Nothing survives a restart.
    /// </summary>
    SingleProcess,
}

/// <summary>Settings of the <c>Hosting</c> configuration section.</summary>
public sealed class HostingOptions
{
    public const string SectionName = "Hosting";

    public HostingMode Mode { get; set; } = HostingMode.Distributed;

    /// <summary>Single-process mode: how many commands the in-memory queue holds before publishing fails.</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 1_000_000)]
    public int InMemoryCommandCapacity { get; set; } = InMemoryMessageBus.DefaultCommandCapacity;
}

/// <summary>
/// Composes how the API reaches the elevator. Either way the endpoints publish commands through
/// <see cref="IElevatorCommandPublisher"/> and read state from the <see cref="ElevatorStateCache"/>; only the
/// implementations of the ports, and where the engine runs, differ.
/// </summary>
internal static class ElevatorHostingExtensions
{
    /// <summary>The connection string Aspire supplies for the RabbitMQ resource, an <c>amqp://</c> URI with credentials.</summary>
    public const string MessagingConnectionName = "messaging";

    public static IHostApplicationBuilder AddElevator(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;
        var hosting = configuration.GetSection(HostingOptions.SectionName).Get<HostingOptions>() ?? new HostingOptions();
        services.AddOptions<HostingOptions>().BindConfiguration(HostingOptions.SectionName).ValidateDataAnnotations().ValidateOnStart();
        services.AddElevatorOptions();
        services.AddHealthChecks().AddCheck<ElevatorStatusHealthCheck>(ElevatorStatusHealthCheck.Name, HealthStatus.Degraded, ["ready"]);

        if (hosting.Mode == HostingMode.SingleProcess)
        {
            AddSingleProcessEngine(services);
            services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddMeter(ElevatorMetrics.MeterName));
        }
        else
        {
            services.AddRabbitMqMessaging(options =>
            {
                if (configuration.GetConnectionString(MessagingConnectionName) is { Length: > 0 } uri)
                {
                    options.Uri = new Uri(uri);
                }
            });
            services.AddOpenTelemetry().WithTracing(tracing => tracing.AddSource(RabbitMqTelemetry.ActivitySourceName));
        }

        return builder;
    }

    private static void AddSingleProcessEngine(IServiceCollection services)
    {
        services.AddSingleton(provider => new InMemoryMessageBus(
            provider.GetRequiredService<ILogger<InMemoryMessageBus>>(),
            commandCapacity: provider.GetRequiredService<IOptions<HostingOptions>>().Value.InMemoryCommandCapacity));
        services.AddSingleton<IElevatorCommandPublisher>(provider => provider.GetRequiredService<InMemoryMessageBus>());
        services.AddSingleton<IElevatorCommandConsumer>(provider => provider.GetRequiredService<InMemoryMessageBus>());
        services.AddSingleton<IElevatorUpdatePublisher>(provider => provider.GetRequiredService<InMemoryMessageBus>());
        services.AddSingleton<IElevatorUpdateSubscriber>(provider => provider.GetRequiredService<InMemoryMessageBus>());

        services.AddSingleton<IElevatorStateStore, InMemoryElevatorStateStore>();
        services.AddSingleton<IProcessedCommandStore, InMemoryProcessedCommandStore>();
        services.AddSingleton<InMemoryLeaderElector>();
        services.AddSingleton<ILeaderElector>(provider => provider.GetRequiredService<InMemoryLeaderElector>());

        services.AddElevatorEngine();
        services.AddHealthChecks().AddCheck<SingleProcessEngineHealthCheck>(SingleProcessEngineHealthCheck.Name, tags: ["ready"]);
    }
}

/// <summary>
/// Readiness: whether the API knows the elevator's state. <c>Degraded</c> until the engine's first update
/// arrives; the API still accepts calls meanwhile, since the broker keeps them for the engine.
/// </summary>
internal sealed class ElevatorStatusHealthCheck(ElevatorStateCache cache) : IHealthCheck
{
    public const string Name = "elevator-status";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(cache.Status is null
            ? new HealthCheckResult(context.Registration.FailureStatus, "No elevator status received from the engine yet.")
            : HealthCheckResult.Healthy("The elevator status is known."));
}

/// <summary>Single-process readiness: the engine inside this process must be driving the car.</summary>
internal sealed class SingleProcessEngineHealthCheck(ElevatorEngine engine) : IHealthCheck
{
    public const string Name = "elevator-engine";

    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(engine.Role == EngineRole.Leader
            ? HealthCheckResult.Healthy("The elevator engine is running in this process.")
            : HealthCheckResult.Unhealthy($"The elevator engine is not running ({engine.Role})."));
}
