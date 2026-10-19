using ElevatorSystem.Domain;

namespace ElevatorSystem.Application;

/// <summary>
/// The immediate answer to a passenger request: accepted with an identifier, or refused with a reason.
/// What happens next (queued, merged, served) is reported through the elevator's events.
/// </summary>
public sealed record RequestResult
{
    private RequestResult(RequestId? requestId, RequestRejectionReason? rejectionReason)
    {
        RequestId = requestId;
        RejectionReason = rejectionReason;
    }

    public RequestId? RequestId { get; }

    public RequestRejectionReason? RejectionReason { get; }

    public bool IsAccepted => RequestId is not null;

    public static RequestResult Accepted(RequestId requestId) => new(requestId, null);

    public static RequestResult Rejected(RequestRejectionReason reason) => new(null, reason);
}
