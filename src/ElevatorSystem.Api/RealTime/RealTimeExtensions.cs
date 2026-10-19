using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.RealTime;

internal static class RealTimeExtensions
{
    /// <summary>
    /// The hub only pushes; clients send nothing but the handshake and pings, so their messages are kept tiny.
    /// </summary>
    public const long MaximumReceiveMessageSize = 4 * 1024;

    public static IHostApplicationBuilder AddElevatorRealTime(this IHostApplicationBuilder builder)
    {
        var services = builder.Services;

        services.AddOptions<RealTimeOptions>().BindConfiguration(RealTimeOptions.Section).ValidateDataAnnotations().ValidateOnStart();
        services.AddSingleton<ElevatorEventBuffer>();
        services.AddSingleton<IElevatorEventSink>(provider => provider.GetRequiredService<ElevatorEventBuffer>());
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
