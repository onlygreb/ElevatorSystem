namespace ElevatorSystem.Infrastructure.Persistence;

// Persistence models: the shape of the tables, kept apart from the Domain types so the schema and
// the domain can evolve independently. Enumerations are stored as fixed lowercase codes (see
// SnapshotMapping), never as Domain enum names or numbers, so renaming a Domain member cannot
// silently change the meaning of stored rows. None of these tables holds personal data (ADR-0024).

/// <summary>The saved state of the elevator: one row per car.</summary>
internal sealed class ElevatorStateRecord
{
    public short Id { get; set; }

    public int CurrentFloor { get; set; }

    public string State { get; set; } = "";

    public string? TravelDirection { get; set; }

    public string? OutOfServiceReason { get; set; }

    public long Version { get; set; }

    public DateTimeOffset TakenAt { get; set; }

    public List<PendingCallRecord> PendingCalls { get; set; } = [];
}

/// <summary>A pending call of a saved snapshot, at its position in the FIFO order.</summary>
internal sealed class PendingCallRecord
{
    public short ElevatorId { get; set; }

    public int Position { get; set; }

    /// <summary>The kind discriminator: <c>hall</c> (with a direction) or <c>car</c> (without).</summary>
    public string Kind { get; set; } = "";

    public int Floor { get; set; }

    public string? Direction { get; set; }

    public Guid FirstRequestId { get; set; }

    public DateTimeOffset FirstRequestedAt { get; set; }

    public int RequestCount { get; set; }
}

/// <summary>A command that was applied, kept until its retention period ends so a redelivery is ignored.</summary>
internal sealed class ProcessedCommandRecord
{
    public Guid CommandId { get; set; }

    public DateTimeOffset ProcessedAt { get; set; }
}
