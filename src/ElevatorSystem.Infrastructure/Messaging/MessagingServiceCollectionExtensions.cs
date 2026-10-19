using ElevatorSystem.Application.Ports;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Infrastructure.Messaging;

public static class MessagingServiceCollectionExtensions
{
    /// <summary>Name of the broker health check, tagged <see cref="ReadinessTag"/>.</summary>
    public const string HealthCheckName = "rabbitmq";

    public const string ReadinessTag = "ready";

    /// <summary>
    /// Registers the RabbitMQ implementations of the four messaging ports, one shared connection, a hosted service that
    /// connects and declares the topology at start, and the broker health check (<c>Degraded</c> while disconnected).
    /// Options come from the <c>Messaging</c> configuration section, then <paramref name="configure"/>, and are
    /// validated when the host starts.
    /// </summary>
    public static IServiceCollection AddRabbitMqMessaging(this IServiceCollection services, Action<MessagingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<MessagingOptions>()
            .Configure<IServiceProvider>(static (options, provider) =>
            {
                if (provider.GetService<IConfiguration>() is { } configuration)
                {
                    MessagingOptions.Bind(configuration.GetSection(MessagingOptions.SectionName), options);
                }
            });
        if (configure is not null)
        {
            options.Configure(configure);
        }

        options.ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<MessagingOptions>, MessagingOptionsValidator>());

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<RabbitMqConnection>();
        services.TryAddSingleton<IElevatorCommandPublisher, RabbitMqElevatorCommandPublisher>();
        services.TryAddSingleton<IElevatorCommandConsumer, RabbitMqElevatorCommandConsumer>();
        services.TryAddSingleton<IElevatorUpdatePublisher, RabbitMqElevatorUpdatePublisher>();
        services.TryAddSingleton<IElevatorUpdateSubscriber, RabbitMqElevatorUpdateSubscriber>();
        services.AddHostedService<RabbitMqConnectionStarter>();
        services.AddHealthChecks().AddCheck<RabbitMqHealthCheck>(HealthCheckName, HealthStatus.Degraded, [ReadinessTag]);

        return services;
    }
}
