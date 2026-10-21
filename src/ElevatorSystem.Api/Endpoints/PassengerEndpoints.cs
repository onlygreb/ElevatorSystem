using ElevatorSystem.Api.Passengers;
using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Api.Security;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ElevatorSystem.Api.Endpoints;

/// <summary>
/// Anonymous passengers who walk in and out of the car themselves (ADR-0026). Every step is published to the engine as
/// a <see cref="PassengerCommandV1"/> and answered with <c>202</c>; the engine checks it against the car (is the door
/// open on your floor? is there room?) and reports the outcome on the hub under the returned request id.
/// </summary>
internal static class PassengerEndpoints
{
    public const string Tag = "Passengers";

    public static IEndpointRouteBuilder MapPassengerEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var passengers = endpoints.MapGroup(string.Empty).WithTags(Tag);

        passengers.MapPost(ApiRoutes.Passengers, JoinAsync)
            .AllowAnonymous()
            .WithName("JoinAsPassenger")
            .WithSummary("Arrive as a new passenger on the lowest floor")
            .WithDescription("Everyone starts on floor 1. The id is random and identifies nobody; keep it to take the next steps.")
            .RequireRateLimiting(RateLimiting.PassengerPolicy)
            .WithRequestSizeLimit()
            .ProducesStepRefusals();

        passengers.MapGet(ApiRoutes.Passenger, GetPassenger)
            .AllowAnonymous()
            .WithName("GetPassenger")
            .WithSummary("Where a passenger is")
            .WithDescription("As the engine last reported it. While the passenger rides, the car's floor is in the elevator status.")
            .RequireRateLimiting(RateLimiting.ReadPolicy)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        passengers.MapPost(ApiRoutes.PassengerHallCall, CallElevatorAsync)
            .AllowAnonymous()
            .WithName("PassengerCallsTheElevator")
            .WithSummary("Press the up or down button on the passenger's floor")
            .RequireRateLimiting(RateLimiting.PassengerPolicy)
            .WithRequestSizeLimit()
            .ProducesStepRefusals();

        passengers.MapPost(ApiRoutes.PassengerBoard, BoardAsync)
            .AllowAnonymous()
            .WithName("PassengerBoards")
            .WithSummary("Walk into the car")
            .WithDescription("Only while its door is open on the passenger's floor and it has room; otherwise the hub reports PassengerRefused.")
            .RequireRateLimiting(RateLimiting.PassengerPolicy)
            .WithRequestSizeLimit()
            .ProducesStepRefusals();

        passengers.MapPost(ApiRoutes.PassengerCarCall, ChooseFloorAsync)
            .AllowAnonymous()
            .WithName("PassengerChoosesAFloor")
            .WithSummary("Choose a floor inside the car")
            .RequireRateLimiting(RateLimiting.PassengerPolicy)
            .WithRequestSizeLimit()
            .ProducesStepRefusals();

        passengers.MapPost(ApiRoutes.PassengerDepart, Depart)
            .AllowAnonymous()
            .WithName("PassengerDeparts")
            .WithSummary("Say the passenger's tab closed")
            .WithDescription("Sent by the browser as its tab goes away (ADR-0036). The passenger leaves the building a few seconds later, unless the same passenger is heard from first, as a reloaded tab is. No body.")
            .RequireRateLimiting(RateLimiting.PassengerPolicy)
            .WithRequestSizeLimit()
            .ProducesStepRefusals();

        passengers.MapPost(ApiRoutes.PassengerLeave, LeaveAsync)
            .AllowAnonymous()
            .WithName("PassengerLeaves")
            .WithSummary("Walk out of the car")
            .WithDescription("Only while its door is open; the passenger stays on that floor.")
            .RequireRateLimiting(RateLimiting.PassengerPolicy)
            .WithRequestSizeLimit()
            .ProducesStepRefusals();

        return endpoints;
    }

    internal static Task<Results<Accepted<PassengerAcceptedResponse>, ProblemHttpResult>> JoinAsync(
        IElevatorCommandPublisher publisher, TimeProvider timeProvider, HttpContext context, CancellationToken cancellationToken) =>
        PublishAsync(Guid.NewGuid(), PassengerAction.Join, null, null, publisher, timeProvider, context, cancellationToken);

    internal static Results<Ok<PassengerDto>, ProblemHttpResult> GetPassenger(
        Guid passengerId, ElevatorStateCache cache, PassengerDepartures departures, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // A reloaded tab asks for its passenger first: they are still here.
        departures.Cancel(passengerId);
        return cache.Passenger(passengerId) is { } passenger
            ? TypedResults.Ok(passenger)
            : TypedResults.Problem(
                detail: "No passenger with this id is in the building. Arrive again as a new passenger.",
                statusCode: StatusCodes.Status404NotFound,
                title: "Unknown passenger",
                extensions: new Dictionary<string, object?> { [ApiRoutes.RejectionReasonExtension] = nameof(PassengerRefusalReason.UnknownPassenger) });
    }

    internal static Results<Accepted, ProblemHttpResult> Depart(Guid passengerId, PassengerDepartures departures, HttpContext context) =>
        departures.Schedule(passengerId) ? TypedResults.Accepted((string?)null) : ElevatorEndpoints.Refused(RejectionReason.Busy, context);

    internal static Task<Results<Accepted<PassengerAcceptedResponse>, ProblemHttpResult>> CallElevatorAsync(
        Guid passengerId,
        PassengerHallCallRequest request,
        IElevatorCommandPublisher publisher,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken) =>
        Enum.IsDefined(request.Direction)
            ? PublishAsync(passengerId, PassengerAction.CallElevator, null, request.Direction, publisher, timeProvider, context, cancellationToken)
            : Task.FromResult<Results<Accepted<PassengerAcceptedResponse>, ProblemHttpResult>>(
                ElevatorEndpoints.Refused(RejectionReason.InvalidDirection, context));

    internal static Task<Results<Accepted<PassengerAcceptedResponse>, ProblemHttpResult>> BoardAsync(
        Guid passengerId, IElevatorCommandPublisher publisher, TimeProvider timeProvider, HttpContext context, CancellationToken cancellationToken) =>
        PublishAsync(passengerId, PassengerAction.Board, null, null, publisher, timeProvider, context, cancellationToken);

    internal static Task<Results<Accepted<PassengerAcceptedResponse>, ProblemHttpResult>> ChooseFloorAsync(
        Guid passengerId,
        PassengerCarCallRequest request,
        IElevatorCommandPublisher publisher,
        Building building,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken) =>
        building.Contains(new Floor(request.Floor))
            ? PublishAsync(passengerId, PassengerAction.ChooseFloor, request.Floor, null, publisher, timeProvider, context, cancellationToken)
            : Task.FromResult<Results<Accepted<PassengerAcceptedResponse>, ProblemHttpResult>>(
                ElevatorEndpoints.Refused(RejectionReason.InvalidFloor, context));

    internal static Task<Results<Accepted<PassengerAcceptedResponse>, ProblemHttpResult>> LeaveAsync(
        Guid passengerId, IElevatorCommandPublisher publisher, TimeProvider timeProvider, HttpContext context, CancellationToken cancellationToken) =>
        PublishAsync(passengerId, PassengerAction.Leave, null, null, publisher, timeProvider, context, cancellationToken);

    private static async Task<Results<Accepted<PassengerAcceptedResponse>, ProblemHttpResult>> PublishAsync(
        Guid passengerId,
        PassengerAction action,
        int? floor,
        CallDirection? direction,
        IElevatorCommandPublisher publisher,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // Any step shows the passenger is still here, so a departure announced by a reloading tab is called off. Every
        // step comes through here, so the departures are resolved once rather than in each handler.
        context.RequestServices.GetRequiredService<PassengerDepartures>().Cancel(passengerId);
        var now = timeProvider.GetUtcNow();
        var command = new PassengerCommandV1(Guid.CreateVersion7(now), now, passengerId, action, floor, direction);
        try
        {
            await publisher.PublishAsync(command, cancellationToken);
        }
        // Never claim "accepted" without the broker's confirm (ADR-0003).
        catch (MessagingUnavailableException)
        {
            return ElevatorEndpoints.Refused(RejectionReason.Busy, context);
        }

        return TypedResults.Accepted((string?)null, new PassengerAcceptedResponse(passengerId, command.CommandId));
    }

    private static RouteHandlerBuilder ProducesStepRefusals(this RouteHandlerBuilder builder) => builder
        .ProducesProblem(StatusCodes.Status400BadRequest)
        .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
        .ProducesProblem(StatusCodes.Status429TooManyRequests)
        .ProducesProblem(StatusCodes.Status503ServiceUnavailable);
}
