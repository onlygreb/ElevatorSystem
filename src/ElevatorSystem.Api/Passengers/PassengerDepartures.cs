using System.Collections.Concurrent;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.Api.Passengers;

/// <summary>
/// Passengers whose browser tab closed (ADR-0036). Each leaves the building <see cref="Grace"/> later (the
/// <see cref="GraceSetting"/>, 5 s by default), unless the same passenger is heard from first: a reloaded tab picks
/// its journey up within moments, and that cancels the departure.
/// </summary>
/// <remarks>
/// Thread-safe. At most <see cref="MaxPending"/> departures wait at once, so a flood of announcements cannot grow the
/// memory without bound; the endpoint is rate-limited as well.
/// </remarks>
internal sealed class PassengerDepartures(
    IElevatorCommandPublisher publisher, TimeProvider timeProvider, IConfiguration configuration, ILogger<PassengerDepartures> logger) : IDisposable
{
    public const string GraceSetting = "Passengers:DepartureGrace";

    public static readonly TimeSpan DefaultGrace = TimeSpan.FromSeconds(5);

    public const int MaxPending = 10_000;

    private readonly ConcurrentDictionary<Guid, ITimer> _pending = new();

    /// <summary>How long a passenger whose tab closed may take to come back: long enough for a reload.</summary>
    public TimeSpan Grace { get; } = configuration.GetValue(GraceSetting, DefaultGrace);

    /// <summary>How many departures are waiting.</summary>
    public int Pending => _pending.Count;

    /// <summary>Sends <paramref name="passengerId"/> out after <see cref="Grace"/>; a departure already waiting is kept.</summary>
    /// <returns><see langword="false"/> when too many departures are already waiting.</returns>
    public bool Schedule(Guid passengerId)
    {
        if (_pending.ContainsKey(passengerId))
        {
            return true;
        }

        if (_pending.Count >= MaxPending)
        {
            return false;
        }

        var timer = timeProvider.CreateTimer(_ => _ = DepartAsync(passengerId), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (_pending.TryAdd(passengerId, timer))
        {
            timer.Change(Grace, Timeout.InfiniteTimeSpan);
        }
        else
        {
            timer.Dispose();
        }

        return true;
    }

    /// <summary>The passenger is still here (a reloaded tab, say): they stay.</summary>
    public void Cancel(Guid passengerId)
    {
        if (_pending.TryRemove(passengerId, out var timer))
        {
            timer.Dispose();
        }
    }

    public void Dispose()
    {
        foreach (var timer in _pending.Values)
        {
            timer.Dispose();
        }

        _pending.Clear();
    }

    private async Task DepartAsync(Guid passengerId)
    {
        if (!_pending.TryRemove(passengerId, out var timer))
        {
            return;
        }

        await timer.DisposeAsync();
        var now = timeProvider.GetUtcNow();
        try
        {
            await publisher.PublishAsync(new PassengerCommandV1(Guid.CreateVersion7(now), now, passengerId, PassengerAction.LeaveBuilding), CancellationToken.None);
        }
        // The engine forgets an idle passenger anyway; a lost departure only delays that.
        catch (Exception exception)
        {
            ApiLog.DepartureNotSent(logger, exception);
        }
    }
}
