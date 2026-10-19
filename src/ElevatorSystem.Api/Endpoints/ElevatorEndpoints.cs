using ElevatorSystem.Application;
using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ElevatorSystem.Api.Endpoints;

/// <summary>
/// The passenger API, version 1, at the routes in <see cref="ApiRoutes"/>. Requests are handed to
/// <see cref="ElevatorController"/>, which answers at once; what happens next is reported on the hub.
/// </summary>
internal static class ElevatorEndpoints
{
    public const string Tag = "Elevator";

    /// <summary>A request body is a floor and a direction; anything near this size is not a passenger request.</summary>
    public const long MaxRequestBodySize = 1024;

    /// <summary>The intake drains in milliseconds, so a busy refusal is worth retrying almost at once.</summary>
    public const int BusyRetryAfterSeconds = 1;

    public static IEndpointRouteBuilder MapElevatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var elevator = endpoints.MapGroup(string.Empty)
            .WithTags(Tag)
            .AllowAnonymous();

        elevator.MapPost(ApiRoutes.HallCalls, RequestElevator)
            .WithName(nameof(RequestElevator))
            .WithSummary("Call the elevator to a floor")
            .WithDescription("A passenger on a landing presses the up or down button. The call is queued in arrival order (FIFO); its progress is reported on the hub.")
            .WithRequestSizeLimit()
            .ProducesRefusals();

        elevator.MapPost(ApiRoutes.CarCalls, RequestDestination)
            .WithName(nameof(RequestDestination))
            .WithSummary("Choose a destination floor")
            .WithDescription("A passenger inside the car presses a floor button. The call is queued in arrival order (FIFO); its progress is reported on the hub.")
            .WithRequestSizeLimit()
            .ProducesRefusals();

        elevator.MapGet(ApiRoutes.Elevator, GetStatus)
            .WithName(nameof(GetStatus))
            .WithSummary("Get the elevator's current status");

        elevator.MapGet(ApiRoutes.Configuration, GetConfiguration)
            .WithName(nameof(GetConfiguration))
            .WithSummary("Get the building and timing settings");

        return endpoints;
    }

    internal static Results<Accepted<RequestAcceptedResponse>, ProblemHttpResult> RequestElevator(
        HallCallRequest request, ElevatorController controller, HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = controller.RequestElevator(request.Floor, ElevatorContractMapper.ToDomain(request.Direction));
        return ToResponse(result, context);
    }

    internal static Results<Accepted<RequestAcceptedResponse>, ProblemHttpResult> RequestDestination(
        CarCallRequest request, ElevatorController controller, HttpContext context, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = controller.RequestDestination(request.Floor);
        return ToResponse(result, context);
    }

    internal static Ok<ElevatorStatusDto> GetStatus(ElevatorController controller, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TypedResults.Ok(ElevatorContractMapper.ToStatus(controller.Elevator.Snapshot()));
    }

    internal static Ok<ElevatorConfigurationDto> GetConfiguration(ElevatorController controller, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return TypedResults.Ok(ElevatorContractMapper.ToConfiguration(controller));
    }

    private static Results<Accepted<RequestAcceptedResponse>, ProblemHttpResult> ToResponse(RequestResult result, HttpContext context)
    {
        if (result.RequestId is { } requestId)
        {
            return TypedResults.Accepted((string?)null, new RequestAcceptedResponse(requestId.Value));
        }

        var reason = ElevatorContractMapper.ToContract(result.RejectionReason!.Value);
        var (status, title, detail) = reason switch
        {
            RejectionReason.InvalidFloor => (StatusCodes.Status400BadRequest, "Invalid floor", "The floor is not served by this elevator."),
            RejectionReason.InvalidDirection => (StatusCodes.Status400BadRequest, "Invalid direction", "No travel is possible in that direction from this floor."),
            RejectionReason.OutOfService => (StatusCodes.Status409Conflict, "Elevator out of service", "The elevator is out of service and accepts no requests until it returns to service."),
            RejectionReason.Busy => (StatusCodes.Status503ServiceUnavailable, "Elevator busy", "Too many requests are waiting to be processed. Try again shortly."),
            _ => throw new ArgumentOutOfRangeException(nameof(result), reason, "Unmapped rejection reason."),
        };

        if (reason == RejectionReason.Busy)
        {
            context.Response.Headers.RetryAfter = BusyRetryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return TypedResults.Problem(
            detail: detail,
            statusCode: status,
            title: title,
            extensions: new Dictionary<string, object?> { [ApiRoutes.RejectionReasonExtension] = reason.ToString() });
    }

    private static RouteHandlerBuilder WithRequestSizeLimit(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBodySize));

    private static RouteHandlerBuilder ProducesRefusals(this RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status409Conflict)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}
