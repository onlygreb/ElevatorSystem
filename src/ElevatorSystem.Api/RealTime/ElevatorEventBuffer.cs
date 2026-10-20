using System.Threading.Channels;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Options;

namespace ElevatorSystem.Api.RealTime;

/// <summary>
/// The hand-off between the update subscription and the hub broadcaster: a bounded buffer of
/// <see cref="ElevatorUpdateV1"/> and <see cref="OccupancyUpdateV1"/> messages that never blocks.
/// </summary>
/// <remarks>
/// When browsers cannot keep up, the oldest update is dropped, counted and logged (at most once per
/// <see cref="DropLogInterval"/>), and the next status message resynchronises every client. Losing a line
/// of the activity feed is acceptable; holding back the subscription is not (ADR-0015).
/// </remarks>
internal sealed class ElevatorEventBuffer
{
    public static readonly TimeSpan DropLogInterval = TimeSpan.FromSeconds(10);

    private readonly Channel<object> _channel;
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
        _channel = Channel.CreateBounded<object>(
            new BoundedChannelOptions(Capacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            },
            _ => OnDropped());
    }

    public int Capacity { get; }

    /// <summary>How many updates have been dropped since start-up.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Holds <see cref="ElevatorUpdateV1"/> and <see cref="OccupancyUpdateV1"/> messages, oldest first.</summary>
    public ChannelReader<object> Reader => _channel.Reader;

    /// <summary>Always returns at once: a full buffer drops its oldest update.</summary>
    public void Publish(ElevatorUpdateV1 update) => _channel.Writer.TryWrite(update);

    /// <summary>Always returns at once: a full buffer drops its oldest update.</summary>
    public void Publish(OccupancyUpdateV1 update) => _channel.Writer.TryWrite(update);

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
