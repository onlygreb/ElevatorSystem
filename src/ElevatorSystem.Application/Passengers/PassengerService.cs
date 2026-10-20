using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;

namespace ElevatorSystem.Application.Passengers;

/// <summary>
/// A passenger's steps, applied to the registry and, for the buttons, to the elevator: calling places a hall call on
/// the passenger's floor and choosing a floor places a car call (ADR-0026). Used by the demo scenarios and the console
/// demo; the engine's command handler does the same for commands from the broker, so it can follow each call through
/// the control loop.
/// </summary>
public sealed class PassengerService(PassengerRegistry registry, ElevatorController controller, TimeProvider timeProvider)
{
    public PassengerRegistry Registry => registry;

    public ElevatorController Controller => controller;

    public PassengerOutcome Join(Guid passengerId, bool simulated = false) => registry.Join(passengerId, simulated);

    public PassengerOutcome CallElevator(Guid passengerId, Direction direction)
    {
        var request = RequestId.New(timeProvider);
        var outcome = registry.Call(passengerId, direction, request.Value);
        return outcome.Succeeded ? Place(passengerId, request, new HallCall(outcome.Floor, direction), outcome) : outcome;
    }

    public PassengerOutcome Board(Guid passengerId) => registry.Board(passengerId);

    public PassengerOutcome ChooseFloor(Guid passengerId, Floor floor)
    {
        var request = RequestId.New(timeProvider);
        var outcome = registry.ChooseFloor(passengerId, floor, request.Value);
        return outcome.Succeeded ? Place(passengerId, request, new CarCall(floor), outcome) : outcome;
    }

    public PassengerOutcome Leave(Guid passengerId) => registry.Leave(passengerId);

    /// <summary>
    /// Presses a call button without becoming a passenger: what someone impatient does while already waiting. The press
    /// is merged into the pending call, or queues a new one; it counts nobody.
    /// </summary>
    public RequestResult PressCallButton(Floor floor, Direction direction) => controller.RequestElevator(floor.Number, direction);

    /// <summary>Maps the elevator's refusal of a call to the passenger's.</summary>
    public static PassengerRefusalReason ToRefusal(RequestRejectionReason reason) => reason switch
    {
        RequestRejectionReason.OutOfService => PassengerRefusalReason.OutOfService,
        RequestRejectionReason.Busy => PassengerRefusalReason.Busy,
        _ => PassengerRefusalReason.InvalidRequest,
    };

    private PassengerOutcome Place(Guid passengerId, RequestId request, ElevatorCall call, PassengerOutcome outcome)
    {
        var result = controller.Submit(new PassengerRequest(request, call, timeProvider.GetUtcNow()));
        if (result.IsAccepted)
        {
            return outcome;
        }

        if (call is HallCall)
        {
            registry.WithdrawCall(passengerId);
        }

        return outcome with { Refusal = ToRefusal(result.RejectionReason!.Value) };
    }
}
