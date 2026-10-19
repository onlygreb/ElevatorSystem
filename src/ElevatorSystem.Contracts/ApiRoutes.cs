namespace ElevatorSystem.Contracts;

/// <summary>HTTP routes of the elevator API, version 1. Shared by the server and the browser client.</summary>
public static class ApiRoutes
{
    public const string Base = "/api/v1";

    /// <summary><c>POST</c> a <see cref="HallCallRequest"/>; answers <c>202</c> with a <see cref="RequestAcceptedResponse"/>.</summary>
    public const string HallCalls = Base + "/hall-calls";

    /// <summary><c>POST</c> a <see cref="CarCallRequest"/>; answers <c>202</c> with a <see cref="RequestAcceptedResponse"/>.</summary>
    public const string CarCalls = Base + "/car-calls";

    /// <summary><c>GET</c> the current <see cref="ElevatorStatusDto"/>.</summary>
    public const string Elevator = Base + "/elevator";

    /// <summary><c>GET</c> the <see cref="ElevatorConfigurationDto"/> (building and timings).</summary>
    public const string Configuration = Base + "/elevator/configuration";

    /// <summary>
    /// Name of the ProblemDetails extension that carries a <see cref="RejectionReason"/> when a request is refused.
    /// </summary>
    public const string RejectionReasonExtension = "reason";
}

/// <summary>The real-time channel: a SignalR hub at <see cref="Path"/> that calls <see cref="IElevatorClient"/>.</summary>
public static class ElevatorHub
{
    public const string Path = "/hubs/elevator";
}
