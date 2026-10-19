using ElevatorSystem.Application.Ports;
using ElevatorSystem.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Messaging.Tests;

/// <summary>What a host gets from <c>AddRabbitMqMessaging</c>, and how its configuration is checked.</summary>
public sealed class MessagingRegistrationTests
{
    [Fact]
    public void AddRabbitMqMessaging_RegistersEveryPortOnOneConnection()
    {
        using var provider = Build(Valid());

        provider.GetRequiredService<IElevatorCommandPublisher>().ShouldBeOfType<RabbitMqElevatorCommandPublisher>();
        provider.GetRequiredService<IElevatorCommandConsumer>().ShouldBeOfType<RabbitMqElevatorCommandConsumer>();
        provider.GetRequiredService<IElevatorUpdatePublisher>().ShouldBeOfType<RabbitMqElevatorUpdatePublisher>();
        provider.GetRequiredService<IElevatorUpdateSubscriber>().ShouldBeOfType<RabbitMqElevatorUpdateSubscriber>();
        provider.GetRequiredService<RabbitMqConnection>().ShouldBeSameAs(provider.GetRequiredService<RabbitMqConnection>());
        provider.GetServices<IHostedService>().ShouldContain(service => service is RabbitMqConnectionStarter);
    }

    [Fact]
    public void AddRabbitMqMessaging_RegistersAReadinessCheckThatDegrades()
    {
        using var provider = Build(Valid());

        var registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(candidate => candidate.Name == MessagingServiceCollectionExtensions.HealthCheckName);

        registration.FailureStatus.ShouldBe(HealthStatus.Degraded);
        registration.Tags.ShouldContain(MessagingServiceCollectionExtensions.ReadinessTag);
    }

    [Fact]
    public void Options_AreBoundFromTheMessagingSection()
    {
        using var provider = Build(new Dictionary<string, string?>(Valid())
        {
            ["Messaging:Port"] = "5673",
            ["Messaging:VirtualHost"] = "elevator",
            ["Messaging:UseTls"] = "true",
            ["Messaging:PublishTimeout"] = "00:00:02",
            ["Messaging:PrefetchCount"] = "16",
            ["Messaging:SubscriberName"] = "api",
        });

        var options = provider.GetRequiredService<IOptions<MessagingOptions>>().Value;

        options.Host.ShouldBe("rabbit.internal");
        options.Port.ShouldBe(5673);
        options.VirtualHost.ShouldBe("elevator");
        options.UseTls.ShouldBeTrue();
        options.PublishTimeout.ShouldBe(TimeSpan.FromSeconds(2));
        options.PrefetchCount.ShouldBe((ushort)16);
        options.SubscriberName.ShouldBe("api");
    }

    [Fact]
    public void Options_ConfigureDelegate_OverridesConfiguration()
    {
        using var provider = Build(Valid(), options => options.Uri = new Uri("amqps://api:pw@broker.example:5671/elevator"));

        var options = provider.GetRequiredService<IOptions<MessagingOptions>>().Value;

        options.Uri.ShouldNotBeNull().Host.ShouldBe("broker.example");
        options.Describe().ShouldBe("amqps://broker.example:5671/elevator");
    }

    [Theory]
    [InlineData("Messaging:UserName", null, "UserName")]
    [InlineData("Messaging:Port", "70000", "Port")]
    [InlineData("Messaging:Port", "not-a-port", "Port")]
    [InlineData("Messaging:PublishTimeout", "00:00:00", "PublishTimeout")]
    [InlineData("Messaging:PrefetchCount", "0", "PrefetchCount")]
    [InlineData("Messaging:SubscriberName", "api.#", "SubscriberName")]
    [InlineData("Messaging:Uri", "http://rabbit.internal", "Uri")]
    public void Options_Invalid_FailValidationNamingTheKeyButNotTheValue(string key, string? value, string reported)
    {
        using var provider = Build(new Dictionary<string, string?>(Valid()) { [key] = value });

        var exception = Should.Throw<OptionsValidationException>(() => provider.GetRequiredService<IOptions<MessagingOptions>>().Value);

        exception.Message.ShouldContain(reported);
        exception.Message.ShouldNotContain("s3cret");
    }

    [Fact]
    public void Options_NeverPrintThePassword()
    {
        var options = new MessagingOptions { Host = "rabbit.internal", UserName = "engine", Password = "s3cret" };
        var fromUri = new MessagingOptions { Uri = new Uri("amqp://engine:s3cret@rabbit.internal/") };

        options.ToString().ShouldNotContain("s3cret");
        fromUri.ToString().ShouldNotContain("s3cret");
        fromUri.ToString().ShouldNotContain("engine");
    }

    private static Dictionary<string, string?> Valid() => new()
    {
        ["Messaging:Host"] = "rabbit.internal",
        ["Messaging:UserName"] = "engine",
        ["Messaging:Password"] = "s3cret",
    };

    private static ServiceProvider Build(Dictionary<string, string?> settings, Action<MessagingOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        services.AddSingleton<ILoggerFactory>(NullLoggerFactory.Instance);
        services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
        services.AddRabbitMqMessaging(configure);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
