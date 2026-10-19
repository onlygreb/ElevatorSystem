using System.Text.Json;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Application.Tests;

/// <summary>The domain-to-contract mapping and the JSON wire format shared by the API and the browser client.</summary>
public sealed class ElevatorContractTests
{
    [Fact]
    public void Enums_MapEveryDomainValueToTheContractValueWithTheSameName()
    {
        foreach (var state in Enum.GetValues<ElevatorState>())
        {
            ElevatorContractMapper.ToContract(state).ToString().ShouldBe(state.ToString());
        }

        foreach (var reason in Enum.GetValues<OutOfServiceReason>())
        {
            ElevatorContractMapper.ToContract(reason).ToString().ShouldBe(reason.ToString());
        }

        foreach (var reason in Enum.GetValues<RequestRejectionReason>())
        {
            ElevatorContractMapper.ToContract(reason).ToString().ShouldBe(reason.ToString());
        }

        foreach (var direction in Enum.GetValues<Direction>())
        {
            ElevatorContractMapper.ToContract(direction).ToString().ShouldBe(direction.ToString());
            ElevatorContractMapper.ToDomain(ElevatorContractMapper.ToContract(direction)).ShouldBe(direction);
        }
    }

    [Fact]
    public void ToDomain_UnknownDirection_StaysInvalidSoTheRequestIsRefused()
    {
        var direction = ElevatorContractMapper.ToDomain((CallDirection)42);

        Building.Default.Validate(new HallCall(new Floor(5), direction)).ShouldBe(RequestRejectionReason.InvalidDirection);
    }

    [Fact]
    public void ToStatus_CopiesTheSnapshot()
    {
        var elevator = new Elevator(Building.Default, new FakeTimeProvider(ControllerHarness.StartTime), new Floor(3));
        elevator.AddRequest(Request(new HallCall(new Floor(7), Direction.Down)));
        elevator.AddRequest(Request(new HallCall(new Floor(7), Direction.Down)));
        elevator.AddRequest(Request(new CarCall(new Floor(2))));
        elevator.MoveUp();

        var status = ElevatorContractMapper.ToStatus(elevator.Snapshot());

        status.CurrentFloor.ShouldBe(4);
        status.Status.ShouldBe(ElevatorStatus.MovingUp);
        status.TravelDirection.ShouldBe(CallDirection.Up);
        status.OutOfServiceCause.ShouldBeNull();
        status.TargetFloors.ShouldBe([7, 2]);
        status.PendingCalls[0].ShouldBe(new PendingCallDto(new CallDto(CallKind.Hall, 7, CallDirection.Down), 2, ControllerHarness.StartTime));
        status.PendingCalls[1].Call.ShouldBe(new CallDto(CallKind.Car, 2, null));
    }

    [Fact]
    public async Task ToActivity_MapsEveryKindOfEventTheControllerPublishes()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        await harness.CallElevatorAsync(3, Direction.Up);
        await harness.CallElevatorAsync(3, Direction.Up);
        await harness.RunUntilIdleAsync();
        await harness.SelectFloorAsync(5);
        harness.Controller.EmergencyStop();
        harness.Controller.ReturnToService();
        harness.Elevator.AddRequest(Request(new CarCall(new Floor(99))));

        var activities = harness.Probe.Events.Select(ElevatorContractMapper.ToActivity).ToList();

        activities.Select(a => a.Kind).Distinct().ToList().ShouldBe(
            [.. Enum.GetValues<ElevatorActivityKind>().Except([ElevatorActivityKind.RequestRejected])],
            ignoreOrder: true,
            "every kind except RequestRejected is produced by this journey");
        var moved = activities.First(a => a.Kind == ElevatorActivityKind.Moved);
        moved.ShouldBe(new ElevatorActivityDto(moved.OccurredAt, ElevatorActivityKind.Moved, 2, FromFloor: 1, Direction: CallDirection.Up));
        var served = activities.Single(a => a.Kind == ElevatorActivityKind.CallServed);
        served.Call.ShouldBe(new CallDto(CallKind.Hall, 3, CallDirection.Up));
        served.RequestCount.ShouldBe(2);
        served.WaitTime.ShouldBe(ControllerHarness.Step * 2);
        var stopped = activities.Single(a => a.Kind == ElevatorActivityKind.OutOfService);
        stopped.OutOfServiceCause.ShouldBe(OutOfServiceCause.EmergencyStop);
        stopped.CancelledCalls.ShouldBe(1);
    }

    [Fact]
    public void ToActivity_RejectedRequest_CarriesTheReason()
    {
        var request = Request(new CarCall(new Floor(4)));
        var rejected = new RequestRejected(ControllerHarness.StartTime, new Floor(4), request, RequestRejectionReason.OutOfService);

        var activity = ElevatorContractMapper.ToActivity(rejected);

        activity.Kind.ShouldBe(ElevatorActivityKind.RequestRejected);
        activity.RequestId.ShouldBe(request.Id.Value);
        activity.RejectionReason.ShouldBe(RejectionReason.OutOfService);
    }

    [Fact]
    public void Json_UsesCamelCasePropertiesAndEnumNames()
    {
        var status = new ElevatorStatusDto(
            4, ElevatorStatus.MovingUp, CallDirection.Up, null,
            [new PendingCallDto(new CallDto(CallKind.Hall, 7, CallDirection.Down), 1, ControllerHarness.StartTime)],
            [7], 12, ControllerHarness.StartTime);

        var json = JsonSerializer.Serialize(status, ContractsJsonContext.Default.ElevatorStatusDto);

        json.ShouldContain("\"currentFloor\":4");
        json.ShouldContain("\"status\":\"MovingUp\"");
        json.ShouldContain("\"travelDirection\":\"Up\"");
        json.ShouldContain("\"kind\":\"Hall\"");
        var roundTrip = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.ElevatorStatusDto)!;
        roundTrip.CurrentFloor.ShouldBe(4);
        roundTrip.PendingCalls.ShouldHaveSingleItem().Call.Direction.ShouldBe(CallDirection.Down);
    }

    [Fact]
    public void Json_ReadsARequestBodyAsTheBrowserSendsIt()
    {
        var request = JsonSerializer.Deserialize("""{"floor":5,"direction":"Down"}""", ContractsJsonContext.Default.HallCallRequest);

        request.ShouldBe(new HallCallRequest(5, CallDirection.Down));
    }

    private static PassengerRequest Request(ElevatorCall call) =>
        new(RequestId.New(TimeProvider.System), call, ControllerHarness.StartTime);
}
