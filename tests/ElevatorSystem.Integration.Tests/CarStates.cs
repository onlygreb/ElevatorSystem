using ElevatorSystem.Contracts;

namespace ElevatorSystem.Integration.Tests;

/// <summary>How the tests recognise the car's states in the statuses the API reports.</summary>
internal static class CarStates
{
    /// <summary>Nothing to do: the car waits with its door open and no countdown (ADR-0025), or idle and closed.</summary>
    public static bool Resting(ElevatorStatusDto status) =>
        status.PendingCalls.Count == 0
        && (status.Status == ElevatorStatus.Idle || status is { Status: ElevatorStatus.DoorOpen, DoorsCloseAt: null });
}
