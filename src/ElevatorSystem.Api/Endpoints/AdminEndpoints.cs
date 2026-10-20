using System.Globalization;
using ElevatorSystem.Api.Security;
using ElevatorSystem.Api.Staff;
using ElevatorSystem.Application.Audit;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Infrastructure.Staff;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace ElevatorSystem.Api.Endpoints;

/// <summary>
/// The admin API (ADR-0017, ADR-0022): service actions for admins, the audit trail for admins and auditors.
/// Admins never operate the car; they take it out of service and return it.
/// </summary>
internal static class AdminEndpoints
{
    public const string Tag = "Admin";
    public const int DefaultAuditPageSize = 50;
    public const int MaxAuditPageSize = 100;

    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var admin = endpoints.MapGroup(string.Empty).WithTags(Tag);

        admin.MapPost(ApiRoutes.ServiceActions, RequestServiceActionAsync)
            .RequireAuthorization(StaffPolicies.ServiceControl)
            .WithName("RequestServiceAction")
            .WithSummary("Start maintenance, stop the car in an emergency, or return it to service")
            .WithDescription("Admins signed in with two factors. The action is written to the audit trail before it is sent to the engine, ahead of queued calls; the result is reported on the hub as a status change. An Idempotency-Key header (a UUID) makes a retry return the first answer.")
            .RequireRateLimiting(RateLimiting.StaffPolicy)
            .WithRequestSizeLimit()
            .RequireAntiforgeryToken()
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status413PayloadTooLarge)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        admin.MapGet(ApiRoutes.AuditEntries, GetAuditEntriesAsync)
            .RequireAuthorization(StaffPolicies.Staff)
            .WithName("GetAuditEntries")
            .WithSummary("Read the audit trail")
            .WithDescription("Newest first. Pass the page's nextBefore as before to read further back.")
            .RequireRateLimiting(RateLimiting.StaffPolicy)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        admin.MapGet(ApiRoutes.AuditVerification, VerifyAuditTrailAsync)
            .RequireAuthorization(StaffPolicies.Staff)
            .WithName("VerifyAuditTrail")
            .WithSummary("Verify the audit trail's hash chain")
            .WithDescription("Recomputes every link (ADR-0014). Note the head hash elsewhere: a later verification that does not reach it means entries were removed from the end.")
            .RequireRateLimiting(RateLimiting.StaffPolicy)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status403Forbidden)
            .ProducesProblem(StatusCodes.Status429TooManyRequests);

        return endpoints;
    }

    internal static async Task<Results<Accepted<ServiceActionAcceptedResponse>, ProblemHttpResult>> RequestServiceActionAsync(
        ServiceActionRequest request,
        [FromHeader(Name = ApiRoutes.IdempotencyKeyHeader)] string? idempotencyKey,
        IElevatorCommandPublisher publisher,
        AuditTrail audit,
        ServiceActionIdempotency idempotency,
        UserManager<StaffUser> users,
        TimeProvider timeProvider,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        // The JSON options refuse unknown names, but an enum also reads from a number.
        if (!Enum.IsDefined(request.Action))
        {
            return Invalid("Unknown service action", "The action must be StartMaintenance, EmergencyStop or ReturnToService.");
        }

        var accountId = StaffAuthEndpoints.AccountId(users, context.User)
            ?? throw new InvalidOperationException("An authorised staff request has no account ID.");

        if (idempotencyKey is null)
        {
            return await ExecuteAsync() is { } accepted ? TypedResults.Accepted((string?)null, accepted) : Unavailable(context);
        }

        if (!Guid.TryParse(idempotencyKey, out var key))
        {
            return Invalid("Invalid Idempotency-Key", "The Idempotency-Key header must be a UUID.");
        }

        var result = await idempotency.RunAsync(accountId, key, request.Action, ExecuteAsync, cancellationToken);
        return result switch
        {
            { Outcome: ServiceActionIdempotency.Outcome.KeyReused } => TypedResults.Problem(
                detail: "This Idempotency-Key was already used for a different action. Use a new key for a new action.",
                statusCode: StatusCodes.Status422UnprocessableEntity,
                title: "Idempotency-Key reused"),
            { Response: { } accepted } => TypedResults.Accepted((string?)null, accepted),
            _ => Unavailable(context),
        };

        async Task<ServiceActionAcceptedResponse?> ExecuteAsync()
        {
            var now = timeProvider.GetUtcNow();
            var command = new ServiceCommandV1(Guid.CreateVersion7(now), now, request.Action);
            var target = string.Create(CultureInfo.InvariantCulture, $"{request.Action} command {command.CommandId}");
            var address = context.Connection.RemoteIpAddress;

            // Audited before it is sent: if the trail cannot be written, nothing is sent (fail closed).
            await audit.RecordAsync(new AuditRecord(AuditActions.ServiceAction, target, AuditOutcomes.Requested, accountId, address), cancellationToken);
            try
            {
                await publisher.PublishAsync(command, cancellationToken);
            }
            catch (MessagingUnavailableException)
            {
                await audit.RecordAsync(new AuditRecord(AuditActions.ServiceAction, target, AuditOutcomes.Refused, accountId, address), cancellationToken);
                return null;
            }

            return new ServiceActionAcceptedResponse(command.CommandId, request.Action);
        }
    }

    internal static async Task<Results<Ok<AuditPageDto>, ProblemHttpResult>> GetAuditEntriesAsync(
        long? before,
        int? limit,
        AuditTrail audit,
        UserManager<StaffUser> users,
        CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultAuditPageSize;
        if (pageSize is < 1 or > MaxAuditPageSize || before is < 1)
        {
            return Invalid("Invalid page", $"limit must be 1-{MaxAuditPageSize} and before a positive sequence.");
        }

        var entries = await audit.ReadPageAsync(before, pageSize, cancellationToken);
        var emails = new Dictionary<Guid, string?>();
        foreach (var actorId in entries.Select(entry => entry.ActorId).OfType<Guid>().Distinct())
        {
            emails[actorId] = (await users.FindByIdAsync(actorId.ToString()))?.Email;
        }

        var page = entries
            .Select(entry => new AuditEntryDto(
                entry.Sequence,
                entry.RecordedAt,
                entry.Action,
                entry.Target,
                entry.Outcome,
                entry.ActorId is { } actor ? emails[actor] : null,
                entry.ClientPseudonym,
                entry.IsAnonymised))
            .ToList();
        return TypedResults.Ok(new AuditPageDto(page, page.Count == pageSize ? page[^1].Sequence : null));
    }

    internal static async Task<Ok<AuditVerificationDto>> VerifyAuditTrailAsync(AuditTrail audit, CancellationToken cancellationToken)
    {
        var result = await audit.VerifyAsync(cancellationToken);
        return TypedResults.Ok(new AuditVerificationDto(
            result.IsIntact,
            result.EntriesChecked,
            result.HeadSequence,
            result.HeadHash.IsEmpty ? null : Convert.ToHexStringLower(result.HeadHash.Span),
            result.FirstBrokenSequence,
            result.Fault?.ToString(),
            result.VerifiedAt));
    }

    private static ProblemHttpResult Invalid(string title, string detail) =>
        TypedResults.Problem(detail: detail, statusCode: StatusCodes.Status400BadRequest, title: title);

    private static ProblemHttpResult Unavailable(HttpContext context) => ElevatorEndpoints.Refused(RejectionReason.Busy, context);
}
