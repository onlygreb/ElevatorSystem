using System.Text.Json;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;

namespace ElevatorSystem.Application.Tests;

/// <summary>Broker message formats and accepting requests created elsewhere (phase 3 groundwork).</summary>
public sealed class ElevatorMessageTests
{
    [Fact]
    public void Commands_SerialiseWithATypeDiscriminatorAndRoundTrip()
    {
        ElevatorCommandV1 hall = new HallCallCommandV1(Guid.CreateVersion7(), ControllerHarness.StartTime, 5, CallDirection.Down);
        ElevatorCommandV1 car = new CarCallCommandV1(Guid.CreateVersion7(), ControllerHarness.StartTime, 2);
        ElevatorCommandV1 service = new ServiceCommandV1(Guid.CreateVersion7(), ControllerHarness.StartTime, ServiceAction.StartMaintenance);

        var hallJson = JsonSerializer.Serialize(hall, ContractsJsonContext.Default.ElevatorCommandV1);
        var carJson = JsonSerializer.Serialize(car, ContractsJsonContext.Default.ElevatorCommandV1);
        var serviceJson = JsonSerializer.Serialize(service, ContractsJsonContext.Default.ElevatorCommandV1);

        hallJson.ShouldContain("\"type\":\"hall-call.v1\"");
        hallJson.ShouldContain("\"direction\":\"Down\"");
        carJson.ShouldContain("\"type\":\"car-call.v1\"");
        serviceJson.ShouldContain("\"type\":\"service.v1\"");
        serviceJson.ShouldContain("\"action\":\"StartMaintenance\"");
        JsonSerializer.Deserialize(hallJson, ContractsJsonContext.Default.ElevatorCommandV1).ShouldBe(hall);
        JsonSerializer.Deserialize(carJson, ContractsJsonContext.Default.ElevatorCommandV1).ShouldBe(car);
        JsonSerializer.Deserialize(serviceJson, ContractsJsonContext.Default.ElevatorCommandV1).ShouldBe(service);
    }

    [Fact]
    public void Commands_UnknownType_IsRejected()
    {
        Should.Throw<JsonException>(() => JsonSerializer.Deserialize(
            """{"type":"open-sesame.v1","commandId":"0199f4a3-0000-7000-8000-000000000000","issuedAt":"2026-01-05T08:00:00Z"}""",
            ContractsJsonContext.Default.ElevatorCommandV1));
    }

    [Fact]
    public void OccupancyUpdate_RoundTrips()
    {
        var update = new OccupancyUpdateV1(
            Guid.CreateVersion7(),
            new BuildingOccupancyDto(3, 8, [new FloorOccupancyDto(1, 2, 5), new FloorOccupancyDto(2, 0, 1)], 17, TimeSpan.FromSeconds(9), ControllerHarness.StartTime));

        var json = JsonSerializer.Serialize(update, ContractsJsonContext.Default.OccupancyUpdateV1);
        var back = JsonSerializer.Deserialize(json, ContractsJsonContext.Default.OccupancyUpdateV1)!;

        json.ShouldContain("\"passengersInCar\":3");
        back.Occupancy.Floors.ShouldBe(update.Occupancy.Floors);
        back.Occupancy.AverageWaitTime.ShouldBe(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task Submit_RequestCreatedElsewhere_KeepsItsIdentifierAndTime()
    {
        await using var harness = ControllerHarness.Create(startFloor: 1).Start();
        var requestedAt = ControllerHarness.StartTime.AddSeconds(-2);
        var request = new PassengerRequest(new RequestId(Guid.CreateVersion7()), new CarCall(new Floor(4)), requestedAt);

        var result = harness.Controller.Submit(request);
        var queued = await harness.NextAsync<CallQueued>();

        result.RequestId.ShouldBe(request.Id);
        queued.Request.ShouldBe(request);
    }

    [Fact]
    public async Task Submit_InvalidRequest_IsRefusedWithoutQueueing()
    {
        await using var harness = ControllerHarness.Create().Start();

        var result = harness.Controller.Submit(new PassengerRequest(new RequestId(Guid.CreateVersion7()), new CarCall(new Floor(0)), ControllerHarness.StartTime));

        result.RejectionReason.ShouldBe(RequestRejectionReason.InvalidFloor);
        harness.Probe.Count.ShouldBe(0);
    }
}
