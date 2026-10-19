using System.Threading.Channels;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Api.RealTime;

/// <summary>
/// The control loop's only contact with the real-time channel: a bounded buffer that never blocks.
/// </summary>
/// <remarks>
/// When browsers cannot keep up, the oldest event is dropped, counted and logged (at most once per
/// <see cref="DropLogInterval"/>), and the next status message resynchronises every client. Losing a
/// line of the activity feed is acceptable; stalling the elevator is not (ADR-0007, ADR-0015).
/// </remarks>
internal sealed class ElevatorEventBuffer : IElevatorEventSink
{
    public static readonly TimeSpan DropLogInterval = TimeSpan.FromSeconds(10);

    private readonly Channel<ElevatorEvent> _channel;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ElevatorEventBuffer> _logger;
    private readonly Lock _dropLogGate = new();
    private long _dropped;
    private long _droppedAtLastLog;
    private DateTimeOffset? _lastDropLog;

    public ElevatorEventBuffer(IOptions<RealTimeOptions> options, TimeProvider timeProvider, ILogger<ElevatorEventBuffer> logger)
    {
        ArgumentNullException.ThrowIfNull(options);

        _timeProvider = timeProvider;
        _logger = logger;
        Capacity = options.Value.EventBufferCapacity;
        _channel = Channel.CreateBounded<ElevatorEvent>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ => OnDropped());
    }

    public int Capacity { get; }

    /// <summary>How many events have been dropped since start-up.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    public ChannelReader<ElevatorEvent> Reader => _channel.Reader;

    /// <summary>Called by the control loop. Always returns at once: a full buffer drops its oldest event.</summary>
    public void Publish(ElevatorEvent elevatorEvent) => _channel.Writer.TryWrite(elevatorEvent);

    private void OnDropped()
    {
        var total = Interlocked.Increment(ref _dropped);
        var now = _timeProvider.GetUtcNow();
        long droppedSinceLastLog;
        lock (_dropLogGate)
        {
            if (_lastDropLog is { } last && now - last < DropLogInterval)
            {
                return;
            }

            droppedSinceLastLog = total - _droppedAtLastLog;
            _droppedAtLastLog = total;
            _lastDropLog = now;
        }

        ApiLog.RealTimeEventsDropped(_logger, droppedSinceLastLog, total, Capacity);
    }
}
