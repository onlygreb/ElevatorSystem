namespace ElevatorSystem.Application.Simulation;

/// <summary>What <see cref="TrafficGenerator"/> has done so far, and where its simulated people are now.</summary>
/// <param name="Arrivals">Simulated people who appeared and pressed a hall button.</param>
/// <param name="Skipped">Arrivals not created because <see cref="TrafficGeneratorOptions.MaxActivePeople"/> were already in the building.</param>
/// <param name="Abandoned">People who gave up because their first call was refused (out of service, busy).</param>
/// <param name="Calling">People whose call is not counted as theirs yet: in flight, or to be placed again.</param>
/// <param name="Waiting">People the occupancy counts include as waiting, left behind or not.</param>
/// <param name="LeftBehind">Waiting people with no pending call, who will call again.</param>
/// <param name="Riding">People in the car.</param>
/// <param name="Delivered">People who got off at the floor they chose.</param>
/// <param name="Evacuated">Riders who got off because the elevator went out of service.</param>
/// <param name="PressesInFlight">Button presses submitted but not yet taken in by the control loop.</param>
public readonly record struct TrafficStatistics(
    int Arrivals,
    int Skipped,
    int Abandoned,
    int Calling,
    int Waiting,
    int LeftBehind,
    int Riding,
    int Delivered,
    int Evacuated,
    int PressesInFlight)
{
    /// <summary>Simulated people still in the building: calling, waiting or riding.</summary>
    public int Active => Calling + Waiting + Riding;
}
