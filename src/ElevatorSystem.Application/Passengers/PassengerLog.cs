using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Passengers;

/// <summary>
/// Source-generated log messages about passengers (event IDs 1840-1859). Passengers are anonymous; even their random
/// ids stay out of the logs, which carry only steps, floors and counts (ADR-0024).
/// </summary>
internal static partial class PassengerLog
{
    [LoggerMessage(1840, LogLevel.Debug, "A passenger's {Action} was refused: {Reason}")]
    public static partial void StepRefused(ILogger logger, PassengerAction action, PassengerRefusalReason reason);

    [LoggerMessage(1841, LogLevel.Information, "The car went out of service at floor {Floor}: {Riders} rider(s) walked out there")]
    public static partial void Evacuated(ILogger logger, Floor floor, int riders);

    [LoggerMessage(1842, LogLevel.Information, "{Count} inactive passenger(s) left the building")]
    public static partial void IdleRemoved(ILogger logger, int count);

    [LoggerMessage(1843, LogLevel.Error, "A passenger observer failed; passengers keep being tracked")]
    public static partial void ObserverFailed(ILogger logger, System.Exception exception);

    [LoggerMessage(1844, LogLevel.Information, "Demo scenario {Scenario} started with {Passengers} simulated passenger(s)")]
    public static partial void ScenarioStarted(ILogger logger, DemoScenario scenario, int passengers);

    [LoggerMessage(1845, LogLevel.Information, "Demo scenarios stopped; {Passengers} simulated passenger(s) left")]
    public static partial void ScenariosStopped(ILogger logger, int passengers);

    [LoggerMessage(1848, LogLevel.Warning, "The queue was not cleared: the controller's intake is full")]
    public static partial void ClearRefused(ILogger logger);

    [LoggerMessage(1846, LogLevel.Warning, "Demo scenario {Scenario} refused: demo scenarios are disabled")]
    public static partial void ScenariosDisabled(ILogger logger, DemoScenario scenario);

    [LoggerMessage(1847, LogLevel.Error, "A simulated passenger's step failed; the scenario carries on")]
    public static partial void SimulatedStepFailed(ILogger logger, System.Exception exception);
}
