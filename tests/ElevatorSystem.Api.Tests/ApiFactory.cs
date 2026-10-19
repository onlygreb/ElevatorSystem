using System.Net;
using ElevatorSystem.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Api.Tests;

/// <summary>
/// The real API host on the in-memory test server, with fast elevator timings so real-time tests
/// finish quickly. Each test that changes the elevator's state or the host's settings gets its own factory.
/// </summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    public static readonly TimeSpan TravelTime = TimeSpan.FromMilliseconds(20);
    public static readonly TimeSpan DoorTime = TimeSpan.FromMilliseconds(20);
    public static readonly DateTimeOffset StartTime = new(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);

    /// <summary>Test-only request header that sets the client address the host sees.</summary>
    public const string ClientAddressHeader = "X-Test-Client-Address";

    private readonly Dictionary<string, string> _settings = new()
    {
        ["Elevator:TravelTimePerFloor"] = TravelTime.ToString("c"),
        ["Elevator:DoorOpenDuration"] = DoorTime.ToString("c"),
        // Generous, so a test host starved of CPU by its neighbours is never mistaken for a stuck car.
        ["Elevator:StuckTolerance"] = "100",
        // Shared test hosts send many requests from one address; tests of the limits set their own.
        ["RateLimiting:Passenger:TokenLimit"] = "100000",
        ["RateLimiting:Read:TokenLimit"] = "100000",
    };

    private readonly List<Action<IServiceCollection>> _services = [];
    private string _environment = Environments.Development;

    /// <summary>The fake clock, when <see cref="WithFakeTime"/> was used.</summary>
    public FakeTimeProvider? Time { get; private set; }

    /// <summary>Overrides one configuration value, for example <c>Elevator:IntakeCapacity</c>.</summary>
    public ApiFactory WithSetting(string key, string value)
    {
        _settings[key] = value;
        return this;
    }

    /// <summary>Drops one of the test overrides, so the host's own configuration applies.</summary>
    public ApiFactory WithoutOverride(string key)
    {
        _settings.Remove(key);
        return this;
    }

    /// <summary>Replaces <see cref="TimeProvider"/> so the test decides when time passes.</summary>
    public ApiFactory WithFakeTime()
    {
        Time = new FakeTimeProvider(StartTime);
        var time = Time;
        return WithServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(time)));
    }

    /// <summary>Runs the host without the control loop, so submitted requests stay in the intake.</summary>
    public ApiFactory WithoutControlLoop() => WithServices(services =>
    {
        var loop = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
            && descriptor.ImplementationType == typeof(ElevatorControlLoopService));
        services.Remove(loop);
    });

    public ApiFactory WithServices(Action<IServiceCollection> configure)
    {
        _services.Add(configure);
        return this;
    }

    public ApiFactory InEnvironment(string environment)
    {
        _environment = environment;
        return this;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        // Logs go to memory, not the console: tests can inspect them, and dozens of hosts running in
        // parallel don't contend for one console writer.
        builder.ConfigureLogging(logging => logging.ClearProviders().AddFakeLogging());
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IStartupFilter, ClientAddressStartupFilter>();
            foreach (var configure in _services)
            {
                configure(services);
            }
        });
    }

    /// <summary>
    /// The in-memory server has no client address; this lets a test pick one per request with the
    /// <see cref="ClientAddressHeader"/> header, ahead of everything else in the pipeline.
    /// </summary>
    private sealed class ClientAddressStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, nextMiddleware) =>
            {
                if (context.Request.Headers.TryGetValue(ClientAddressHeader, out var address))
                {
                    context.Connection.RemoteIpAddress = IPAddress.Parse(address.ToString());
                }

                return nextMiddleware(context);
            });
            next(app);
        };
    }
}
