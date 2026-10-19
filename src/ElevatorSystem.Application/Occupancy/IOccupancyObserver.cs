using ElevatorSystem.Contracts;

namespace ElevatorSystem.Application.Occupancy;

/// <summary>Told by <see cref="OccupancyTracker"/> every time the passenger counts change.</summary>
/// <remarks>
/// Called from the control loop, under the tracker's lock and in the order the counts changed, so
/// implementations must be thread-safe and return quickly: hand the counts to a buffer (the bus, the
/// hub) rather than doing I/O inline. An observer that throws is logged and ignored.
/// </remarks>
public interface IOccupancyObserver
{
    void OccupancyChanged(BuildingOccupancyDto occupancy);
}
