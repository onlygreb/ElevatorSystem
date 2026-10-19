using ElevatorSystem.Contracts;

namespace ElevatorSystem.Web.Services;

/// <summary>The answer to a hall or car call: accepted with an id, or refused with a message a passenger can read.</summary>
public sealed record RequestOutcome
{
    private RequestOutcome(Guid? requestId, RejectionReason? reason, string? message)
    {
        RequestId = requestId;
        Reason = reason;
        Message = message;
    }

    public Guid? RequestId { get; }

    /// <summary>Why the elevator refused the request; null when it was accepted or never reached the elevator.</summary>
    public RejectionReason? Reason { get; }

    /// <summary>A localised, human explanation of the refusal; null when accepted.</summary>
    public string? Message { get; }

    public bool IsAccepted => RequestId is not null;

    public static RequestOutcome Accepted(Guid requestId) => new(requestId, null, null);

    public static RequestOutcome Refused(RejectionReason? reason, string message)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new(null, reason, message);
    }
}
