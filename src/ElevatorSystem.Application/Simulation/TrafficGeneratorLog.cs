using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Simulation;

/// <summary>
/// Source-generated log messages for the traffic generator (event IDs 1820-1839). Simulated people
/// are anonymous counts; nothing here describes a real person (ADR-0009).
/// </summary>
internal static partial class TrafficGeneratorLog
{
    [LoggerMessage(1820, LogLevel.Information, "Traffic generator is disabled; no simulated calls will be placed")]
    public static partial void Disabled(ILogger logger);

    [LoggerMessage(1821, LogLevel.Warning, "Traffic generator started: {ArrivalsPerMinute} arrival(s) per minute, at most {MaxActivePeople} at once, seed {Seed}. This is a test tool")]
    public static partial void Started(ILogger logger, double arrivalsPerMinute, int maxActivePeople, int seed);

    [LoggerMessage(1822, LogLevel.Information, "Traffic generator stopped after {Arrivals} simulated arrival(s)")]
    public static partial void Stopped(ILogger logger, int arrivals);

    [LoggerMessage(1823, LogLevel.Debug, "Skipped a simulated arrival: {Active} simulated people are already in the building")]
    public static partial void ArrivalSkipped(ILogger logger, int active);

    [LoggerMessage(1824, LogLevel.Information, "Traffic generator placed all {MaxPeople} simulated people")]
    public static partial void AllPlaced(ILogger logger, int maxPeople);

    [LoggerMessage(1825, LogLevel.Error, "Traffic generator failed to place a simulated arrival; it keeps running")]
    public static partial void ArrivalFailed(ILogger logger, Exception exception);
}
