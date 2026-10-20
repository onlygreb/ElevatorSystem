using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests;

/// <summary>
/// Every change moves the version up by exactly one: subscribers keep the newest state by comparing versions
/// (ADR-0016), so a change that kept or lowered the version would be lost on its way to the browser.
/// </summary>
public sealed class ElevatorVersionTests
{
    public static TheoryData<string> Changes() =>
    [
        "request queued",
        "request merged",
        "request served at the open door",
        "move up",
        "move down",
        "open door",
        "close door",
        "out of service",
        "back in service",
    ];

    [Theory]
    [MemberData(nameof(Changes))]
    public void EveryChange_RaisesTheVersionByOne(string change)
    {
        var elevator = Before(change);
        var before = elevator.Snapshot().Version;

        var changed = Apply(elevator, change);

        changed.ShouldBeTrue($"'{change}' should succeed from the state the test prepared");
        elevator.Snapshot().Version.ShouldBe(before + 1);
    }

    [Fact]
    public void AJourney_NeverRepeatsAVersion()
    {
        var elevator = At(1);
        var versions = new List<long> { elevator.Snapshot().Version };

        elevator.AddRequest(Request(Car(3)));
        versions.Add(elevator.Snapshot().Version);
        elevator.MoveUp();
        versions.Add(elevator.Snapshot().Version);
        elevator.MoveUp();
        versions.Add(elevator.Snapshot().Version);
        elevator.OpenDoor();
        versions.Add(elevator.Snapshot().Version);
        elevator.CloseDoor();
        versions.Add(elevator.Snapshot().Version);

        versions.ShouldBe([0, 1, 2, 3, 4, 5]);
    }

    private static Elevator Before(string change) => change switch
    {
        "request merged" => WithPending(At(1), Car(6)),
        "request served at the open door" or "close door" => InState(ElevatorState.DoorOpen, 4),
        "move down" => At(5),
        "back in service" => InState(ElevatorState.OutOfService),
        _ => At(3),
    };

    private static bool Apply(Elevator elevator, string change) => change switch
    {
        "request queued" => elevator.AddRequest(Request(Car(6))).Outcome == AddRequestOutcome.Queued,
        "request merged" => elevator.AddRequest(Request(Car(6))).Outcome == AddRequestOutcome.Merged,
        "request served at the open door" => elevator.AddRequest(Request(Car(4))).Outcome == AddRequestOutcome.ServedImmediately,
        "move up" => elevator.MoveUp().Succeeded,
        "move down" => elevator.MoveDown().Succeeded,
        "open door" => elevator.OpenDoor().Succeeded,
        "close door" => elevator.CloseDoor().Succeeded,
        "out of service" => elevator.TakeOutOfService(OutOfServiceReason.Fault).Succeeded,
        "back in service" => elevator.ReturnToService().Succeeded,
        _ => throw new ArgumentOutOfRangeException(nameof(change), change, null),
    };

    private static Elevator WithPending(Elevator elevator, ElevatorCall call)
    {
        elevator.AddRequest(Request(call)).Outcome.ShouldBe(AddRequestOutcome.Queued);
        return elevator;
    }
}
