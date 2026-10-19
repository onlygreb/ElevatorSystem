namespace ElevatorSystem.Domain;

/// <summary>What the car is doing.</summary>
public enum ElevatorState
{
    /// <summary>Stopped at a floor with the door closed (<c>IDLE</c>).</summary>
    Idle,

    /// <summary>Travelling upwards (<c>MOVING_UP</c>).</summary>
    MovingUp,

    /// <summary>Travelling downwards (<c>MOVING_DOWN</c>).</summary>
    MovingDown,

    /// <summary>Stopped at a floor with the door open (<c>DOOR_OPEN</c>).</summary>
    DoorOpen,

    /// <summary>Unavailable to passengers; see <see cref="OutOfServiceReason"/>.</summary>
    OutOfService,
}

/// <summary>Why the car is out of service.</summary>
public enum OutOfServiceReason
{
    /// <summary>Taken out of service by an operator.</summary>
    Maintenance,

    /// <summary>A fault was detected, such as a trip that never arrived.</summary>
    Fault,

    /// <summary>An emergency stop was triggered.</summary>
    EmergencyStop,
}
