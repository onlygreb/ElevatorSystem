using System.Text.Json.Serialization;

namespace ElevatorSystem.Contracts;

// Broker messages, version 1 (ADR-0003). The API publishes passenger calls and admin service
// commands to the engine; the engine publishes an update after every elevator change and after every
// occupancy change. Passengers place calls; the admin only changes the service state (ADR-0017).
// Additive changes only: a breaking change is a V2 type, and consumers accept both until V1 is
// retired. Serialised with ContractsJsonContext; the "type" property names the concrete command.

/// <summary>A command for the engine. For passenger calls, <see cref="CommandId"/> is the request id returned to the caller.</summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(HallCallCommandV1), "hall-call.v1")]
[JsonDerivedType(typeof(CarCallCommandV1), "car-call.v1")]
[JsonDerivedType(typeof(ServiceCommandV1), "service.v1")]
[JsonDerivedType(typeof(PassengerCommandV1), "passenger.v1")]
[JsonDerivedType(typeof(ScenarioCommandV1), "scenario.v1")]
[JsonDerivedType(typeof(SchedulingCommandV1), "scheduling.v1")]
public abstract record ElevatorCommandV1(Guid CommandId, DateTimeOffset IssuedAt);

/// <summary>A passenger on <see cref="Floor"/> wants to go <see cref="Direction"/>.</summary>
public sealed record HallCallCommandV1(Guid CommandId, DateTimeOffset IssuedAt, int Floor, CallDirection Direction)
    : ElevatorCommandV1(CommandId, IssuedAt);

/// <summary>A passenger inside the car chose <see cref="Floor"/>.</summary>
public sealed record CarCallCommandV1(Guid CommandId, DateTimeOffset IssuedAt, int Floor)
    : ElevatorCommandV1(CommandId, IssuedAt);

/// <summary>
/// An admin changes the elevator's service state. Applied immediately, ahead of queued calls.
/// Admins never place calls or move the car (ADR-0017).
/// </summary>
public sealed record ServiceCommandV1(Guid CommandId, DateTimeOffset IssuedAt, ServiceAction Action)
    : ElevatorCommandV1(CommandId, IssuedAt);

/// <summary>
/// An anonymous passenger takes a step (ADR-0026): <see cref="Floor"/> is set for <see cref="PassengerAction.ChooseFloor"/>,
/// <see cref="Direction"/> for <see cref="PassengerAction.CallElevator"/>.
/// </summary>
public sealed record PassengerCommandV1(
    Guid CommandId, DateTimeOffset IssuedAt, Guid PassengerId, PassengerAction Action, int? Floor = null, CallDirection? Direction = null)
    : ElevatorCommandV1(CommandId, IssuedAt);

/// <summary>An admin starts a demo scenario, or stops them all and removes their passengers (<see cref="Scenario"/> null; ADR-0027).</summary>
public sealed record ScenarioCommandV1(Guid CommandId, DateTimeOffset IssuedAt, DemoScenario? Scenario)
    : ElevatorCommandV1(CommandId, IssuedAt);

/// <summary>An admin switches the scheduling strategy; applied only while the car is out of service (ADR-0033).</summary>
public sealed record SchedulingCommandV1(Guid CommandId, DateTimeOffset IssuedAt, Scheduling Scheduling)
    : ElevatorCommandV1(CommandId, IssuedAt);

public enum ServiceAction
{
    /// <summary>Take the elevator out of service for maintenance; pending calls are cancelled.</summary>
    StartMaintenance,

    /// <summary>Stop the car at once and take it out of service; pending calls are cancelled.</summary>
    EmergencyStop,

    /// <summary>Return an out-of-service elevator to service; with nothing to do it opens its door and waits.</summary>
    ReturnToService,
}

/// <summary>
/// Published by the engine after every elevator change: what happened and the status right after it.
/// <see cref="UpdateId"/> is unique per update, so subscribers can drop redeliveries.
/// </summary>
public sealed record ElevatorUpdateV1(Guid UpdateId, ElevatorActivityDto Activity, ElevatorStatusDto Status);

/// <summary>Published by the engine whenever the number of people waiting, riding or present changes.</summary>
public sealed record OccupancyUpdateV1(Guid UpdateId, BuildingOccupancyDto Occupancy);
