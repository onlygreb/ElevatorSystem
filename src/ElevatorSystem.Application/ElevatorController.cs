using System.Threading.Channels;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging;
using Log = ElevatorSystem.Application.ElevatorControllerLog;

namespace ElevatorSystem.Application;

/// <summary>
/// Accepts passenger requests from any thread and drives one <see cref="Domain.Elevator"/> through
/// them in the order chosen by its scheduling strategy.
/// </summary>
/// <remarks>
/// <para>
/// Requests enter a bounded channel and are applied by a single control loop
/// (<see cref="ProcessRequests"/>), which also starts every trip and door cycle. While the car is
/// travelling the loop keeps applying new requests, so assigning a request never waits for the car.
/// </para>
/// <para>
/// Each trip is watched: one that does not arrive within <see cref="ElevatorControllerOptions.StuckTimeout"/>
/// takes the elevator out of service. Safety commands (<see cref="EmergencyStop"/>,
/// <see cref="StartMaintenance"/>) bypass the queue and take effect immediately.
/// See ADR-0004, ADR-0007 and ADR-0012.
/// </para>
/// </remarks>
public sealed class ElevatorController
{
    private readonly Channel<PassengerRequest> _intake;
    private readonly ISchedulingStrategy _strategy;
    private readonly IElevatorDrive _drive;
    private readonly IElevatorEventSink _eventSink;
    private readonly ElevatorControllerOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<ElevatorController> _logger;
    private readonly Lock _operationGate = new();
    private CancellationTokenSource? _currentOperation;
    private int _isProcessing;

    public ElevatorController(
        Elevator elevator,
        ISchedulingStrategy strategy,
        IElevatorDrive drive,
        IElevatorEventSink eventSink,
        ElevatorControllerOptions options,
        TimeProvider timeProvider,
        ILogger<ElevatorController> logger)
    {
        ArgumentNullException.ThrowIfNull(elevator);
        ArgumentNullException.ThrowIfNull(strategy);
        ArgumentNullException.ThrowIfNull(drive);
        ArgumentNullException.ThrowIfNull(eventSink);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);
        options.Validate();

        Elevator = elevator;
        _strategy = strategy;
        _drive = drive;
        _eventSink = eventSink;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;
        _intake = Channel.CreateBounded<PassengerRequest>(new BoundedChannelOptions(options.IntakeCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
    }

    public Elevator Elevator { get; }

    public ISchedulingStrategy Strategy => _strategy;

    public ElevatorControllerOptions Options => _options;

    /// <summary>
    /// A pickup request from a landing: a passenger on <paramref name="floor"/> wants to go in
    /// <paramref name="direction"/>. Safe to call from any thread; never blocks.
    /// </summary>
    public RequestResult RequestElevator(int floor, Direction direction) =>
        Submit(new HallCall(new Floor(floor), direction));

    /// <summary>A destination request from inside the car. Safe to call from any thread; never blocks.</summary>
    public RequestResult RequestDestination(int floor) => Submit(new CarCall(new Floor(floor)));

    /// <summary>
    /// Submits a request created elsewhere, keeping its identifier and time: for example a command
    /// received from the message bus, whose id the API already returned to the caller.
    /// Safe to call from any thread; never blocks.
    /// </summary>
    public RequestResult Submit(PassengerRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var call = request.Call;
        var rejection = Elevator.Building.Validate(call)
            ?? (Elevator.State == ElevatorState.OutOfService ? RequestRejectionReason.OutOfService : (RequestRejectionReason?)null);
        if (rejection is { } invalid)
        {
            Log.RequestRejected(_logger, call, invalid);
            return RequestResult.Rejected(invalid);
        }

        if (!_intake.Writer.TryWrite(request))
        {
            Log.RequestRejected(_logger, call, RequestRejectionReason.Busy);
            return RequestResult.Rejected(RequestRejectionReason.Busy);
        }

        return RequestResult.Accepted(request.Id);
    }

    /// <summary>Stops the car at once and takes it out of service, cancelling every pending call.</summary>
    public TransitionResult EmergencyStop() => TakeOutOfService(OutOfServiceReason.EmergencyStop);

    /// <summary>Takes the car out of service for maintenance, cancelling every pending call.</summary>
    public TransitionResult StartMaintenance() => TakeOutOfService(OutOfServiceReason.Maintenance);

    /// <summary>Returns an out-of-service car to service, idle with the door closed.</summary>
    public TransitionResult ReturnToService()
    {
        var result = Elevator.ReturnToService();
        Publish(result.Events);
        return result;
    }

    /// <summary>
    /// Runs the control loop until <paramref name="cancellationToken"/> is cancelled: applies requests,
    /// moves the car floor by floor, cycles the door at each stop, and watches every trip.
    /// </summary>
    /// <exception cref="InvalidOperationException">The loop is already running: an elevator has exactly one.</exception>
    public async Task ProcessRequests(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _isProcessing, 1) == 1)
        {
            throw new InvalidOperationException("The control loop is already running; an elevator has exactly one.");
        }

        Log.ControlLoopStarted(_logger, Elevator.CurrentFloor, _strategy.Name);
        var events = new List<ElevatorEvent>();
        Operation? operation = null;
        Task<bool>? requestArrival = null;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    ApplyPendingRequests(events);
                    if (operation is { Completion.IsCompleted: true })
                    {
                        FinishOperation(operation, events);
                        operation = null;
                    }

                    operation ??= StartNextOperation(events, cancellationToken);
                }
                // Fail-safe: any unexpected failure must stop the car, not crash the loop.
                catch (Exception exception)
                {
                    Log.ControlLoopFailed(_logger, exception);
                    CancelCurrentOperation();
                    events.AddRange(Elevator.TakeOutOfService(OutOfServiceReason.Fault).Events);
                }

                // Publish only after the next operation has started, so an observer reacting to these
                // events always finds the controller in a consistent state.
                Publish(events);
                events.Clear();

                // Sleep until a request arrives, the current trip or door cycle ends, or shutdown.
                requestArrival ??= _intake.Reader.WaitToReadAsync(cancellationToken).AsTask();
                Task wakeUp = operation is null
                    ? Task.WhenAny(requestArrival)
                    : Task.WhenAny(requestArrival, operation.Completion);
                await wakeUp.ConfigureAwait(false);

                if (requestArrival.IsCompleted)
                {
                    if (!requestArrival.IsCompletedSuccessfully || !await requestArrival.ConfigureAwait(false))
                    {
                        break;
                    }

                    requestArrival = null;
                }
            }
        }
        finally
        {
            CancelCurrentOperation();
            if (operation is not null)
            {
                EndOperation(operation.Cancellation);
            }

            Volatile.Write(ref _isProcessing, 0);
            Log.ControlLoopStopped(_logger);
        }
    }

    private RequestResult Submit(ElevatorCall call) =>
        Submit(new PassengerRequest(RequestId.New(_timeProvider), call, _timeProvider.GetUtcNow()));

    private TransitionResult TakeOutOfService(OutOfServiceReason reason)
    {
        // The elevator's own lock makes this take effect immediately, whatever the loop is doing.
        var result = Elevator.TakeOutOfService(reason);
        CancelCurrentOperation();
        Publish(result.Events);
        return result;
    }

    private void ApplyPendingRequests(List<ElevatorEvent> events)
    {
        while (_intake.Reader.TryRead(out var request))
        {
            try
            {
                events.AddRange(Elevator.AddRequest(request).Events);
            }
            // One bad request must never stop the elevator; it is logged and dropped.
            catch (Exception exception)
            {
                Log.RequestFailed(_logger, exception, request.Id);
            }
        }
    }

    private Operation? StartNextOperation(List<ElevatorEvent> events, CancellationToken loopToken)
    {
        var snapshot = Elevator.Snapshot();
        switch (snapshot.State)
        {
            case ElevatorState.OutOfService:
                return null;
            case ElevatorState.DoorOpen:
                return StartDoorCycle(loopToken);
        }

        var isMoving = snapshot.State is ElevatorState.MovingUp or ElevatorState.MovingDown;
        if (_strategy.SelectTarget(snapshot) is not { } target)
        {
            // Nothing left to serve: a car that is still moving stops at the floor it is reaching.
            return isMoving ? OpenDoorHere(events, loopToken) : null;
        }

        if (target == snapshot.CurrentFloor)
        {
            return OpenDoorHere(events, loopToken);
        }

        var direction = target > snapshot.CurrentFloor ? Direction.Up : Direction.Down;
        var mustReverse = snapshot.State == (direction == Direction.Up ? ElevatorState.MovingDown : ElevatorState.MovingUp);
        if (mustReverse)
        {
            // The car only changes direction after stopping.
            return OpenDoorHere(events, loopToken);
        }

        var move = direction == Direction.Up ? Elevator.MoveUp() : Elevator.MoveDown();
        if (!move.Succeeded)
        {
            Log.TransitionRefused(_logger, direction == Direction.Up ? nameof(Elevator.MoveUp) : nameof(Elevator.MoveDown), snapshot.CurrentFloor, move.Error!.Value);
            return null;
        }

        events.AddRange(move.Events);
        return StartTrip(direction, loopToken);
    }

    private Operation? OpenDoorHere(List<ElevatorEvent> events, CancellationToken loopToken)
    {
        var opened = Elevator.OpenDoor();
        if (!opened.Succeeded)
        {
            Log.TransitionRefused(_logger, nameof(Elevator.OpenDoor), Elevator.CurrentFloor, opened.Error!.Value);
            return null;
        }

        events.AddRange(opened.Events);
        return StartDoorCycle(loopToken);
    }

    private Operation StartTrip(Direction direction, CancellationToken loopToken)
    {
        var cancellation = BeginOperation(loopToken);
        Task trip;
        try
        {
            trip = _drive.MoveOneFloorAsync(direction, cancellation.Token);
        }
        // A drive that throws is a fault like any other: the watchdog path handles it.
        catch (Exception exception)
        {
            trip = Task.FromException(exception);
        }

        var watched = trip.WaitAsync(_options.StuckTimeout, _timeProvider, cancellation.Token);
        return new Operation(OperationKind.Trip, watched, cancellation);
    }

    private Operation StartDoorCycle(CancellationToken loopToken)
    {
        var cancellation = BeginOperation(loopToken);
        var dwell = Task.Delay(_options.DoorOpenDuration, _timeProvider, cancellation.Token);
        return new Operation(OperationKind.DoorCycle, dwell, cancellation);
    }

    private void FinishOperation(Operation operation, List<ElevatorEvent> events)
    {
        EndOperation(operation.Cancellation);
        var completion = operation.Completion;
        if (completion.IsCanceled)
        {
            // Stopped by a safety command or shutdown; the elevator's state already reflects it.
            return;
        }

        if (completion.Exception is { } failure)
        {
            var cause = failure.GetBaseException();
            if (cause is TimeoutException)
            {
                Log.ElevatorStuck(_logger, Elevator.CurrentFloor, _options.StuckTimeout);
            }
            else
            {
                Log.DriveFailed(_logger, cause, Elevator.CurrentFloor);
            }

            events.AddRange(Elevator.TakeOutOfService(OutOfServiceReason.Fault).Events);
            return;
        }

        if (operation.Kind == OperationKind.DoorCycle)
        {
            events.AddRange(Elevator.CloseDoor().Events);
        }

        // A completed trip needs nothing here: the next decision stops at this floor or continues.
    }

    private CancellationTokenSource BeginOperation(CancellationToken loopToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(loopToken);
        lock (_operationGate)
        {
            _currentOperation = cancellation;
        }

        // A safety command may have landed between the decision and this point; honour it.
        if (Elevator.State == ElevatorState.OutOfService)
        {
            cancellation.Cancel();
        }

        return cancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        lock (_operationGate)
        {
            if (ReferenceEquals(_currentOperation, cancellation))
            {
                _currentOperation = null;
            }
        }

        cancellation.Dispose();
    }

    private void CancelCurrentOperation()
    {
        CancellationTokenSource? current;
        lock (_operationGate)
        {
            current = _currentOperation;
        }

        try
        {
            current?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The operation finished at the same moment; there is nothing left to cancel.
        }
    }

    private void Publish(IReadOnlyList<ElevatorEvent> events)
    {
        foreach (var elevatorEvent in events)
        {
            LogEvent(elevatorEvent);
            try
            {
                _eventSink.Publish(elevatorEvent);
            }
            // A failing subscriber must never stop the elevator (ADR-0007).
            catch (Exception exception)
            {
                Log.EventSinkFailed(_logger, exception, elevatorEvent.GetType().Name);
            }
        }
    }

    private void LogEvent(ElevatorEvent elevatorEvent)
    {
        switch (elevatorEvent)
        {
            case CallQueued queued:
                Log.CallQueued(_logger, queued.Request.Call, queued.Request.Id, queued.Position);
                break;
            case CallMerged merged:
                Log.CallMerged(_logger, merged.Request.Id, merged.Request.Call, merged.Position);
                break;
            case RequestRejected rejected:
                Log.RequestRejected(_logger, rejected.Request.Call, rejected.Reason);
                break;
            case ElevatorMoved moved:
                Log.ElevatorMoved(_logger, moved.Direction, moved.From, moved.Floor);
                break;
            case DoorsOpened opened:
                Log.DoorsOpened(_logger, opened.Floor);
                break;
            case CallServed served:
                Log.CallServed(_logger, served.Call.Call, served.Call.RequestCount, served.WaitTime);
                break;
            case DoorsClosed closed:
                Log.DoorsClosed(_logger, closed.Floor);
                break;
            case ElevatorTakenOutOfService outOfService:
                Log.TakenOutOfService(_logger, outOfService.Floor, outOfService.Reason, outOfService.CancelledCalls.Length);
                break;
            case ElevatorReturnedToService returned:
                Log.ReturnedToService(_logger, returned.Floor);
                break;
        }
    }

    private enum OperationKind
    {
        Trip,
        DoorCycle,
    }

    private sealed record Operation(OperationKind Kind, Task Completion, CancellationTokenSource Cancellation);
}
