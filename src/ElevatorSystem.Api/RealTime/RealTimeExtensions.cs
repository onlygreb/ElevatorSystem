using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.RealTime;

internal static class RealTimeExtensions
{
    /// <summary>
    /// The hub only pushes; clients send nothing but the handshake and pings, so their messages are kept tiny.
    /// </summary>
    public const long MaximumReceiveMessageSize = 4 * 1024;

    /// <summary>
    /// Registers the update subscription, the cache of the latest state it feeds, the lossy buffer and the hub
    /// broadcaster (ADR-0015). The <c>IElevatorUpdateSubscriber</c> comes from the hosting mode.
    /// </summary>
    public static IHostApplicationBuilder AddElevatorRealTime(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddOptions<RealTimeOptions>().BindConfiguration(RealTimeOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ElevatorStateCache>();
        services.AddSingleton<ElevatorEventBuffer>();
        services.AddHostedService<ElevatorUpdateSubscriptionService>();
        services.AddHostedService<ElevatorBroadcastService>();

        services.AddSignalR(options =>
            {
                options.EnableDetailedErrors = false;
                options.MaximumReceiveMessageSize = MaximumReceiveMessageSize;
            })
            .AddJsonProtocol(options => ContractsJson.Apply(options.PayloadSerializerOptions));

        return builder;
    }

    public static IEndpointRouteBuilder MapElevatorHub(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapHub<ElevatorStatusHub>(ElevatorHub.Path).AllowAnonymous();
        return endpoints;
    }
}
