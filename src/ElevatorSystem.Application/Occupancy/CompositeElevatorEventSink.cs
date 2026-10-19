using System.Collections.Immutable;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging;
using Log = ElevatorSystem.Application.Occupancy.OccupancyLog;

namespace ElevatorSystem.Application.Occupancy;

/// <summary>
/// Hands every elevator event to several sinks in the order given, for example the
/// <see cref="OccupancyTracker"/> first and then the real-time fan-out.
/// </summary>
/// <remarks>
/// The list is fixed at construction, so publishing is thread-safe and takes no lock. A sink that
/// throws is logged (event ID 1806) and skipped; the sinks after it still receive the event.
/// Each sink must itself be thread-safe and fast, as <see cref="IElevatorEventSink"/> requires.
/// </remarks>
public sealed class CompositeElevatorEventSink : IElevatorEventSink
{
    private readonly ImmutableArray<IElevatorEventSink> _sinks;
    private readonly ILogger<CompositeElevatorEventSink> _logger;

    public CompositeElevatorEventSink(IEnumerable<IElevatorEventSink> sinks, ILogger<CompositeElevatorEventSink> logger)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(logger);

        _sinks = [.. sinks];
        if (_sinks.Any(sink => sink is null))
        {
            throw new ArgumentException("Every sink must be set.", nameof(sinks));
        }

        _logger = logger;
    }

    public ImmutableArray<IElevatorEventSink> Sinks => _sinks;

    public void Publish(ElevatorEvent elevatorEvent)
    {
        ArgumentNullException.ThrowIfNull(elevatorEvent);

        foreach (var sink in _sinks)
        {
            try
            {
                sink.Publish(elevatorEvent);
            }
            catch (Exception exception)
            {
                Log.SinkFailed(_logger, exception, sink.GetType().Name, elevatorEvent.GetType().Name);
            }
        }
    }
}
