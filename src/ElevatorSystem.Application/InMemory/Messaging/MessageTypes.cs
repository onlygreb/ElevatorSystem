using ElevatorSystem.Contracts;

namespace ElevatorSystem.Application.InMemory.Messaging;

/// <summary>Message type names for logs and traces; commands use their JSON type discriminator.</summary>
internal static class MessageTypes
{
    public const string ElevatorUpdate = "elevator-update.v1";
    public const string OccupancyUpdate = "occupancy-update.v1";

    public static string Of(ElevatorCommandV1 command) => command switch
    {
        HallCallCommandV1 => "hall-call.v1",
        CarCallCommandV1 => "car-call.v1",
        ServiceCommandV1 => "service.v1",
        _ => command.GetType().Name,
    };
}
