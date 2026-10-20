using ElevatorSystem.Domain;
using Microsoft.Extensions.Logging;

namespace ElevatorSystem.Application.Hosting;

/// <summary>
/// Source-generated log messages of the engine's hosting cycle (event IDs 2000-2099). Commands and
/// updates are named by ID and type only, never by content (ADR-0009).
/// </summary>
internal static partial class ElevatorEngineLog
{
    [LoggerMessage(2000, LogLevel.Information, "Standing by: waiting to become the leader")]
    public static partial void StandingBy(ILogger logger);

    [LoggerMessage(2001, LogLevel.Information, "Became the leader: driving the elevator")]
    public static partial void BecameLeader(ILogger logger);

    [LoggerMessage(2002, LogLevel.Information, "Restored the elevator at floor {Floor} ({State}, {PendingCalls} pending call(s), version {Version})")]
    public static partial void Restored(ILogger logger, Floor floor, ElevatorState state, int pendingCalls, long version);

    [LoggerMessage(2003, LogLevel.Information, "No saved state: starting at floor {Floor}")]
    public static partial void StartingFresh(ILogger logger, Floor floor);

    [LoggerMessage(2004, LogLevel.Warning, "Leadership lost: stopped driving the elevator")]
    public static partial void LeadershipLost(ILogger logger);

    [LoggerMessage(2005, LogLevel.Information, "Stopped leading for shutdown at floor {Floor}")]
    public static partial void StoppedLeading(ILogger logger, Floor floor);

    [LoggerMessage(2006, LogLevel.Warning, "Loading the saved elevator state failed; retrying in {RetryInterval}")]
    public static partial void LoadFailed(ILogger logger, Exception exception, TimeSpan retryInterval);

    [LoggerMessage(2007, LogLevel.Error, "The saved elevator state could not be restored; starting from a fresh state")]
    public static partial void RestoreFailed(ILogger logger, Exception exception);

    [LoggerMessage(2008, LogLevel.Information, "Start-up door cycle at floor {Floor}")]
    public static partial void StartUpDoorCycle(ILogger logger, Floor floor);

    [LoggerMessage(2009, LogLevel.Information, "Stopped the car at floor {Floor} with its door open for shutdown")]
    public static partial void Parked(ILogger logger, Floor floor);

    [LoggerMessage(2010, LogLevel.Warning, "Saving the elevator state failed; retrying every {RetryInterval} while the car keeps operating")]
    public static partial void SaveFailed(ILogger logger, Exception exception, TimeSpan retryInterval);

    [LoggerMessage(2011, LogLevel.Information, "Saving the elevator state works again (version {Version})")]
    public static partial void SaveRecovered(ILogger logger, long version);

    [LoggerMessage(2012, LogLevel.Warning, "Publishing elevator updates failed; subscribers keep the last state they received until the broker is back")]
    public static partial void PublishFailed(ILogger logger, Exception exception);

    [LoggerMessage(2013, LogLevel.Information, "Publishing elevator updates works again")]
    public static partial void PublishRecovered(ILogger logger);

    [LoggerMessage(2014, LogLevel.Warning, "Update buffer full: dropped {DroppedSinceLastReport} update(s) ({TotalDropped} since start-up, capacity {Capacity}); the next status supersedes them")]
    public static partial void UpdatesDropped(ILogger logger, long droppedSinceLastReport, long totalDropped, int capacity);

    [LoggerMessage(2015, LogLevel.Information, "Dropped command {CommandId} ({CommandType}): it was already applied")]
    public static partial void DuplicateCommand(ILogger logger, Guid commandId, string commandType);

    [LoggerMessage(2016, LogLevel.Warning, "Could not check command {CommandId} ({CommandType}) for duplicates; applying it anyway")]
    public static partial void DeduplicationUnavailable(ILogger logger, Exception exception, Guid commandId, string commandType);

    [LoggerMessage(2017, LogLevel.Warning, "Service command {CommandId} ({Action}) refused: {Error}")]
    public static partial void ServiceCommandRefused(ILogger logger, Guid commandId, string action, TransitionError error);

    [LoggerMessage(2018, LogLevel.Warning, "Ignored command {CommandId} ({CommandType}): unknown type or value")]
    public static partial void UnknownCommand(ILogger logger, Guid commandId, string commandType);

    [LoggerMessage(2019, LogLevel.Warning, "The last state was not fully saved and published within {Timeout} of stopping")]
    public static partial void ShutdownFlushIncomplete(ILogger logger, TimeSpan timeout);

    [LoggerMessage(2020, LogLevel.Warning, "Releasing leadership failed; the lock is freed when the connection closes")]
    public static partial void ReleaseFailed(ILogger logger, Exception exception);

    [LoggerMessage(2021, LogLevel.Information, "Service command {CommandId} applied: {Action}")]
    public static partial void ServiceCommandApplied(ILogger logger, Guid commandId, string action);
}
