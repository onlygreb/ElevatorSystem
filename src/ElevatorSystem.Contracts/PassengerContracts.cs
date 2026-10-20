namespace ElevatorSystem.Contracts;

/// <summary>Answers a passenger's step: the passenger it applies to and the id that reports its progress on the hub.</summary>
/// <param name="PassengerId">The anonymous passenger, a random id the browser keeps.</param>
/// <param name="RequestId">Every activity this step causes carries it as <see cref="ElevatorActivityDto.RequestId"/>.</param>
public sealed record PassengerAcceptedResponse(Guid PassengerId, Guid RequestId);

/// <summary>A passenger on a landing presses the up or down button of their floor.</summary>
public sealed record PassengerHallCallRequest(CallDirection Direction);

/// <summary>A passenger inside the car chooses a floor.</summary>
public sealed record PassengerCarCallRequest(int Floor);

/// <summary>Where an anonymous passenger is, as the engine last reported it (ADR-0026).</summary>
/// <param name="PassengerId">The passenger.</param>
/// <param name="Floor">The floor they are on, or the car's floor while they ride.</param>
/// <param name="InCar">Riding the car.</param>
/// <param name="Waiting">The direction they called, while they wait on their floor for the car.</param>
/// <param name="Destination">The floor they chose inside the car, until they leave.</param>
/// <param name="Simulated">A passenger of a demo scenario rather than a person.</param>
public sealed record PassengerDto(Guid PassengerId, int Floor, bool InCar, CallDirection? Waiting, int? Destination, bool Simulated);

/// <summary>What a passenger does (ADR-0026).</summary>
public enum PassengerAction
{
    /// <summary>Arrive in the building, on the lowest floor.</summary>
    Join,

    /// <summary>Press the up or down button on their floor.</summary>
    CallElevator,

    /// <summary>Walk into the car, while its door is open at their floor.</summary>
    Board,

    /// <summary>Choose a floor inside the car.</summary>
    ChooseFloor,

    /// <summary>Walk out of the car, while its door is open.</summary>
    Leave,
}

/// <summary>Why a passenger's step was refused; the <c>passengerRefusal</c> of a <c>PassengerRefused</c> activity.</summary>
public enum PassengerRefusalReason
{
    /// <summary>The engine does not know this passenger (it may have restarted): join again.</summary>
    UnknownPassenger,

    /// <summary>The building holds as many passengers as it allows.</summary>
    BuildingFull,

    /// <summary>The car's door is not open at the passenger's floor.</summary>
    DoorNotOpenHere,

    /// <summary>The car carries as many people as it can.</summary>
    CarFull,

    /// <summary>The step needs the passenger inside the car.</summary>
    NotInCar,

    /// <summary>The step needs the passenger on a floor, not inside the car.</summary>
    InCar,

    /// <summary>The car's door is closed, so nobody can walk out.</summary>
    DoorClosed,

    /// <summary>The floor or direction does not exist from here.</summary>
    InvalidRequest,

    /// <summary>The elevator is out of service.</summary>
    OutOfService,

    /// <summary>The elevator cannot take another call right now; try again shortly.</summary>
    Busy,
}

/// <summary>The demo scenarios an admin can start (ADR-0027). Every simulated passenger starts on the lowest floor.</summary>
public enum DemoScenario
{
    /// <summary>A dozen people arrive in the lobby and ride up to their floors.</summary>
    MorningRush,

    /// <summary>People ride up, stay a while, and come back down to the lobby.</summary>
    LunchBreak,

    /// <summary>A steady stream of people moving between floors for a couple of minutes.</summary>
    BusyDay,

    /// <summary>
    /// Hundreds of people, many of them pressing the call button again and again, while the car and every simulation
    /// run fast-forwarded (ADR-0031): a stress test of the whole system.
    /// </summary>
    HugeLoad,
}

/// <summary>An admin starts a demo scenario.</summary>
public sealed record StartScenarioRequest(DemoScenario Scenario);

/// <summary>A scenario command passed to the engine; simulated passengers appear on the hub as they act.</summary>
public sealed record ScenarioAcceptedResponse(Guid CommandId);
