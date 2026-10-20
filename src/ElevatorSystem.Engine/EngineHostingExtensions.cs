using ElevatorSystem.Application.Diagnostics;
using ElevatorSystem.Application.Hosting;
using ElevatorSystem.Infrastructure.Messaging;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace ElevatorSystem.Engine;

/// <summary>
/// Composes the engine host: the elevator engine on RabbitMQ and PostgreSQL, migrations before it competes
/// for leadership, and health that reports its role (ADR-0021).
/// </summary>
internal static class EngineHostingExtensions
{
    /// <summary>The connection string Aspire supplies for the RabbitMQ resource, an <c>amqp://</c> URI with credentials.</summary>
    public const string MessagingConnectionName = "messaging";

    public const string HealthCheckName = "elevator-engine";

    public const string RolePath = "/health/role";

    public static IHostApplicationBuilder AddElevatorEngineHost(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;
        var configuration = builder.Configuration;

        services.AddRabbitMqMessaging(options =>
        {
            if (configuration.GetConnectionString(MessagingConnectionName) is { Length: > 0 } uri)
            {
                options.Uri = new Uri(uri);
            }
        });
        // Hosted services start in registration order, so the schema exists before the retention job and the
        // engine touch it (ADR-0019 §7).
        services.AddHostedService<DatabaseMigrationService>();
        services.AddElevatorPersistence();
        services.AddElevatorEngine();

        services.AddHealthChecks().AddCheck<EngineHealthCheck>(HealthCheckName, tags: ["ready"]);
        services.AddOpenTelemetry().WithTracing(tracing => tracing
            .AddSource(RabbitMqTelemetry.ActivitySourceName)
            .AddSource("Npgsql"))
            .WithMetrics(metrics => metrics.AddMeter(ElevatorMetrics.MeterName));

        return builder;
    }

    /// <summary>
    /// <c>GET /health/role</c> answers <c>leader</c>, <c>standby</c>, <c>starting</c> or <c>stopped</c>, so
    /// operators and tests can tell the replicas apart. Mapped where the other health endpoints are.
    /// </summary>
    public static WebApplication MapEngineRole(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapGet(RolePath, (ElevatorEngine engine) => RoleName(engine.Role));
        }

        return app;
    }

    internal static string RoleName(EngineRole role) => role.ToString().ToLowerInvariant();
}

/// <summary>
/// Readiness of the engine: ready once it competes for leadership, as leader or standby. Starting (migrating)
/// or stopped is unhealthy. The role is in the description and the data.
/// </summary>
internal sealed class EngineHealthCheck(ElevatorEngine engine) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var role = engine.Role;
        var data = new Dictionary<string, object> { ["role"] = EngineHostingExtensions.RoleName(role) };
        return Task.FromResult(role switch
        {
            EngineRole.Leader => HealthCheckResult.Healthy("Leading: driving the elevator.", data),
            EngineRole.Standby => HealthCheckResult.Healthy("Standing by for leadership.", data),
            EngineRole.Starting => HealthCheckResult.Unhealthy("Starting: preparing the database.", data: data),
            _ => HealthCheckResult.Unhealthy("Stopped.", data: data),
        });
    }
}
