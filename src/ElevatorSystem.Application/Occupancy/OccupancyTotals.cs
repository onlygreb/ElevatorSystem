namespace ElevatorSystem.Application.Occupancy;

/// <summary>
/// Building-wide totals behind the occupancy counts, for diagnostics and tests. People are conserved:
/// <c>Waiting + LeftBehind + Riding + Present == PeopleCounted</c>, and no total is ever negative.
/// </summary>
/// <param name="PeopleCounted">Distinct people the tracker has counted since it started.</param>
/// <param name="Waiting">People waiting for the elevator whose hall call is still pending.</param>
/// <param name="LeftBehind">
/// People waiting with no pending call: the car was full when it served their call, or the call was
/// cancelled when the elevator went out of service. They have to call again. A floor's
/// <c>Waiting</c> in <see cref="Contracts.FloorOccupancyDto"/> is <see cref="Waiting"/> plus these.
/// </param>
/// <param name="Riding">People in the car.</param>
/// <param name="Present">People on a floor who are not waiting.</param>
/// <param name="Delivered">Riders who got off at a floor they chose.</param>
/// <param name="Evacuated">Riders who got off because the elevator went out of service.</param>
/// <param name="Boarded">People who boarded the car; the average wait is taken over them.</param>
/// <param name="Corrections">How many times inconsistent events were clamped (each one is logged).</param>
public readonly record struct OccupancyTotals(
    int PeopleCounted,
    int Waiting,
    int LeftBehind,
    int Riding,
    int Present,
    int Delivered,
    int Evacuated,
    int Boarded,
    int Corrections);
