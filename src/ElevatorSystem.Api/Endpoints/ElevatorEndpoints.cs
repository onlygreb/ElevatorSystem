using System.Globalization;
using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Api.Security;
using ElevatorSystem.Application;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ElevatorSystem.Api.Endpoints;

/// <summary>
/// The passenger API, version 1, at the routes in <see cref="ApiRoutes"/>. A valid call is published to the
/// engine as a command and answered with <c>202</c> once the broker has confirmed it; what happens next is
/// reported on the hub. Reads come from the latest state the engine reported (ADR-0021).
/// </summary>
internal static class ElevatorEndpoints
{
    public const string Tag = "Elevator";

    /// <summary>A request body is a floor and a direction; anything near this size is not a passenger request.</summary>
    public const long MaxRequestBodySize = 1024;

    /// <summary>The broker reconnects within seconds, so a refused call is worth retrying shortly.</summary>
    public const int UnavailableRetryAfterSeconds = 2;

    /// <summary>The engine re-sends its state every few seconds, so a status that is not yet known soon will be.</summary>
    public const int StatusUnknownRetryAfterSeconds = 2;

    public static IEndpointRouteBuilder MapElevatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var elevator = endpoints.MapGroup(string.Empty)
            .WithTags(Tag)
            .AllowAnonymous();

        elevator.MapPost(ApiRoutes.HallCalls, RequestElevator)
            .WithName(nameof(RequestElevator))
            .WithSummary("Call the elevator to a floor")
            .WithDescription("A passenger on a landing presses the up or down button. The call is queued in arrival order (FIFO); its progress is reported on the hub.")
            .RequireRateLimiting(RateLimiting.PassengerPolicy)
            .WithRequestSizeLimit()
            .ProducesRefusals();

        elevator.MapPost(ApiRoutes.CarCalls, RequestDestination)
            .WithName(nameof(RequestDestination))
            .WithSummary("Choose a destination floor")
            .WithDescription("A passenger inside the car presses a floor button. The call is queued in arrival order (FIFO); its progress is reported on the hub.")
            .RequireRateLimiting(RateLimiting.PassengerPolicy)
            .WithRequestSizeLimit()
            .ProducesRefusals();

        elevator.MapGet(ApiRoutes.Elevator, GetStatus)
            .WithName(nameof(GetStatus))
            .WithSummary("Get the elevator's current status")
            .WithDescription("The latest status the engine reported. 503 with Retry-After until the engine has reported one.")
            .RequireRateLimiting(RateLimiting.ReadPolicy)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        elevator.MapGet(ApiRoutes.Configuration, GetConfiguration)
            .WithName(nameof(GetConfiguration))
            .WithSummary("Get the building and timing settings")
            .RequireRateLimiting(RateLimiting.ReadPolicy)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        elevator.MapGet(ApiRoutes.Occupancy, GetOccupancy)
            .WithName(nameof(GetOccupancy))
            .WithSummary("Get how many people are waiting, riding and present")
            .WithDescription("Counts estimated from the calls people place (ADR-0017); nobody is identified. 503 with Retry-After until the engine has reported them.")
            .RequireRateLimiting(RateLimiting.ReadPolicy)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    internal static Task<Results<Accepted<RequestAcceptedResponse>, ProblemHttpResult>> RequestElevator(
        HallCallRequest request,
        IElevatorCommandPublisher publisher,
        ElevatorStateCache cache,
        Building building,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var call = new HallCall(new Floor(request.Floor), ElevatorContractMapper.ToDomain(request.Direction));
        var now = timeProvider.GetUtcNow();
        return SubmitAsync(call, new HallCallCommandV1(Guid.CreateVersion7(now), now, request.Floor, request.Direction), publisher, cache, building, context, cancellationToken);
    }

    internal static Task<Results<Accepted<RequestAcceptedResponse>, ProblemHttpResult>> RequestDestination(
        CarCallRequest request,
        IElevatorCommandPublisher publisher,
        ElevatorStateCache cache,
        Building building,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var call = new CarCall(new Floor(request.Floor));
        var now = timeProvider.GetUtcNow();
        return SubmitAsync(call, new CarCallCommandV1(Guid.CreateVersion7(now), now, request.Floor), publisher, cache, building, context, cancellationToken);
    }

    internal static Results<Ok<ElevatorStatusDto>, ProblemHttpResult> GetStatus(ElevatorStateCache cache, HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return cache.Status is { } status
            ? TypedResults.Ok(status)
            : NotYetKnown(context, "Elevator status not yet known", "The elevator engine has not reported its status yet. Try again shortly.");
    }

    internal static Ok<ElevatorConfigurationDto> GetConfiguration(
        Building building, ElevatorControllerOptions options, ISchedulingStrategy strategy, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TypedResults.Ok(new ElevatorConfigurationDto(
            building.LowestFloor.Number, building.HighestFloor.Number, options.TravelTimePerFloor, options.DoorOpenDuration, strategy.Name));
    }

    internal static Results<Ok<BuildingOccupancyDto>, ProblemHttpResult> GetOccupancy(ElevatorStateCache cache, HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return cache.Occupancy is { } occupancy
            ? TypedResults.Ok(occupancy)
            : NotYetKnown(context, "Occupancy not yet known", "The elevator engine has not reported the passenger counts yet. Try again shortly.");
    }

    private static async Task<Results<Accepted<RequestAcceptedResponse>, ProblemHttpResult>> SubmitAsync(
        ElevatorCall call,
        ElevatorCommandV1 command,
        IElevatorCommandPublisher publisher,
        ElevatorStateCache cache,
        Building building,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // Refuse what can be refused here; the engine checks again and reports anything it refuses on the hub.
        var rejection = building.Validate(call)
            ?? (cache.Status?.Status == ElevatorStatus.OutOfService ? RequestRejectionReason.OutOfService : (RequestRejectionReason?)null);
        if (rejection is { } reason)
        {
            return Refused(ElevatorContractMapper.ToContract(reason), context);
        }

        try
        {
            await publisher.PublishAsync(command, cancellationToken);
        }
        // Never claim "accepted" without the broker's confirm (ADR-0003).
        catch (MessagingUnavailableException)
        {
            return Refused(RejectionReason.Busy, context);
        }

        return TypedResults.Accepted((string?)null, new RequestAcceptedResponse(command.CommandId));
    }

    private static ProblemHttpResult Refused(RejectionReason reason, HttpContext context)
    {
        var (status, title, detail) = reason switch
        {
            RejectionReason.InvalidFloor => (StatusCodes.Status400BadRequest, "Invalid floor", "The floor is not served by this elevator."),
            RejectionReason.InvalidDirection => (StatusCodes.Status400BadRequest, "Invalid direction", "No travel is possible in that direction from this floor."),
            RejectionReason.OutOfService => (StatusCodes.Status409Conflict, "Elevator out of service", "The elevator is out of service and accepts no requests until it returns to service."),
            RejectionReason.Busy => (StatusCodes.Status503ServiceUnavailable, "Elevator unavailable", "The request could not be passed to the elevator right now. Try again shortly."),
            _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "Unmapped rejection reason."),
        };

        if (reason == RejectionReason.Busy)
        {
            context.Response.Headers.RetryAfter = UnavailableRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        return TypedResults.Problem(
            detail: detail,
            statusCode: status,
            title: title,
            extensions: new Dictionary<string, object?> { [ApiRoutes.RejectionReasonExtension] = reason.ToString() });
    }

    private static ProblemHttpResult NotYetKnown(HttpContext context, string title, string detail)
    {
        context.Response.Headers.RetryAfter = StatusUnknownRetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        return TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status503ServiceUnavailable, title: title);
    }

    private static RouteHandlerBuilder WithRequestSizeLimit(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBodySize));

    private static RouteHandlerBuilder ProducesRefusals(this RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}
