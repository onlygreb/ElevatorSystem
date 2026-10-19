using System.Collections.Concurrent;
using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.SignalR;

namespace ElevatorSystem.Api.Tests.RealTime;

/// <summary>
/// Stands in for SignalR: records every message sent to clients and lets a test decide how each send
/// behaves (completes, throws, or never completes, like a client that stopped reading).
/// </summary>
internal sealed class FakeHubContext : IHubContext<ElevatorStatusHub, IElevatorClient>, IHubClients<IElevatorClient>, IElevatorClient
{
    private readonly Func<object, Task> _send;
    private readonly ConcurrentQueue<object> _attempts = new();

    public FakeHubContext(Func<object, Task>? send = null)
    {
        _send = send ?? (_ => Task.CompletedTask);
    }

    public static FakeHubContext Throwing() => new(_ => throw new InvalidOperationException("The transport is broken."));

    /// <summary>Every send blocks forever, as if a client had stopped reading its socket.</summary>
    public static FakeHubContext Hanging() => new(_ => new TaskCompletionSource().Task);

    /// <summary>Every message the broadcaster tried to send, in order, whether or not the send succeeded.</summary>
    public IReadOnlyList<object> Attempts => [.. _attempts];

    public IReadOnlyList<ElevatorActivityDto> Activities => [.. _attempts.OfType<ElevatorActivityDto>()];

    public IReadOnlyList<ElevatorStatusDto> Statuses => [.. _attempts.OfType<ElevatorStatusDto>()];

    IHubClients<IElevatorClient> IHubContext<ElevatorStatusHub, IElevatorClient>.Clients => this;

    IGroupManager IHubContext<ElevatorStatusHub, IElevatorClient>.Groups => throw new NotSupportedException("The elevator hub uses no groups.");

    public IElevatorClient All => this;

    public Task StatusChanged(ElevatorStatusDto status) => Record(status);

    public Task ActivityOccurred(ElevatorActivityDto activity) => Record(activity);

    public Task OccupancyChanged(BuildingOccupancyDto occupancy) => Record(occupancy);

    public IElevatorClient AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw Unexpected();

    public IElevatorClient Client(string connectionId) => throw Unexpected();

    IElevatorClient IHubClients<IElevatorClient>.Clients(IReadOnlyList<string> connectionIds) => throw Unexpected();

    public IElevatorClient Group(string groupName) => throw Unexpected();

    public IElevatorClient GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw Unexpected();

    IElevatorClient IHubClients<IElevatorClient>.Groups(IReadOnlyList<string> groupNames) => throw Unexpected();

    public IElevatorClient User(string userId) => throw Unexpected();

    public IElevatorClient Users(IReadOnlyList<string> userIds) => throw Unexpected();

    private static NotSupportedException Unexpected() => new("The broadcaster only sends to all clients.");

    private Task Record(object message)
    {
        _attempts.Enqueue(message);
        return _send(message);
    }
}
