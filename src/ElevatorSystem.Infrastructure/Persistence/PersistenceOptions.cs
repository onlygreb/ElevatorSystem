using Microsoft.Extensions.Options;

namespace ElevatorSystem.Infrastructure.Persistence;

/// <summary>Settings for the PostgreSQL persistence, bound from the <c>Persistence</c> configuration section.</summary>
public sealed class PersistenceOptions
{
    public const string SectionName = "Persistence";

    /// <summary>The name under <c>ConnectionStrings</c> that holds the database's connection string.</summary>
    public string ConnectionStringName { get; set; } = "elevatordb";

    /// <summary>How long a processed command ID is remembered. It must outlast any redelivery of that command.</summary>
    public TimeSpan ProcessedCommandRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How often expired processed command IDs are purged.</summary>
    public TimeSpan ProcessedCommandPurgeInterval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    /// The PostgreSQL advisory lock key that elects the leader. Every engine of one installation must
    /// use the same key; the default spells "Elevator" in ASCII.
    /// </summary>
    public long LeaderLockId { get; set; } = 0x456C_6576_6174_6F72;

    /// <summary>How long a standby engine waits between attempts to take the leader lock.</summary>
    public TimeSpan LeaderAcquireRetryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How often the leader checks its lock connection. Loss is detected within two intervals, and a
    /// new leader waits that long after taking the lock before it starts (ADR-0019).
    /// </summary>
    public TimeSpan LeaderCheckInterval { get; set; } = TimeSpan.FromSeconds(1);
}

/// <summary>Rejects settings that would break retention or leader election, at start-up.</summary>
internal sealed class PersistenceOptionsValidator : IValidateOptions<PersistenceOptions>
{
    internal static readonly TimeSpan MinimumCheckInterval = TimeSpan.FromMilliseconds(50);
    internal static readonly TimeSpan MaximumCheckInterval = TimeSpan.FromMinutes(1);
    internal static readonly TimeSpan MinimumRetention = TimeSpan.FromHours(1);

    public ValidateOptionsResult Validate(string? name, PersistenceOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.ConnectionStringName))
        {
            failures.Add($"{PersistenceOptions.SectionName}:{nameof(options.ConnectionStringName)} is required.");
        }

        if (options.ProcessedCommandRetention < MinimumRetention)
        {
            failures.Add($"{PersistenceOptions.SectionName}:{nameof(options.ProcessedCommandRetention)} must be at least {MinimumRetention}, so redeliveries are still recognised.");
        }

        if (options.ProcessedCommandPurgeInterval <= TimeSpan.Zero)
        {
            failures.Add($"{PersistenceOptions.SectionName}:{nameof(options.ProcessedCommandPurgeInterval)} must be positive.");
        }

        if (options.LeaderAcquireRetryInterval <= TimeSpan.Zero)
        {
            failures.Add($"{PersistenceOptions.SectionName}:{nameof(options.LeaderAcquireRetryInterval)} must be positive.");
        }

        if (options.LeaderCheckInterval < MinimumCheckInterval || options.LeaderCheckInterval > MaximumCheckInterval)
        {
            failures.Add($"{PersistenceOptions.SectionName}:{nameof(options.LeaderCheckInterval)} must be between {MinimumCheckInterval} and {MaximumCheckInterval}.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
