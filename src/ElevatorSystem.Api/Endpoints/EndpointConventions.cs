using ElevatorSystem.Contracts;
using Microsoft.AspNetCore.Antiforgery;

namespace ElevatorSystem.Api.Endpoints;

/// <summary>Conventions shared by every feature's endpoints.</summary>
internal static class EndpointConventions
{
    /// <summary>A request body is a handful of short fields; anything near this size is not a real request.</summary>
    public const long MaxRequestBodySize = 1024;

    /// <summary>The <c>reason</c> of the problem returned when the antiforgery token is missing or wrong.</summary>
    public const string AntiforgeryReason = "Antiforgery";

    public static RouteHandlerBuilder WithRequestSizeLimit(this RouteHandlerBuilder builder) =>
        builder.WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(MaxRequestBodySize));

    /// <summary>
    /// Refuses the request with a <c>400</c> problem unless it carries a valid antiforgery token in the header named
    /// by <see cref="AntiforgeryTokenDto.HeaderName"/>, matching the antiforgery cookie and the signed-in user.
    /// Every staff <c>POST</c> uses it: the session cookie alone never authorises a state change (ADR-0022).
    /// </summary>
    public static RouteHandlerBuilder RequireAntiforgeryToken(this RouteHandlerBuilder builder) => builder
        .AddEndpointFilter(async (context, next) =>
        {
            var antiforgery = context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>();
            return await antiforgery.IsRequestValidAsync(context.HttpContext)
                ? await next(context)
                : TypedResults.Problem(
                    detail: "Fetch a token from " + ApiRoutes.Antiforgery + " and send it in its header.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Missing or invalid antiforgery token",
                    extensions: new Dictionary<string, object?> { [ApiRoutes.RejectionReasonExtension] = AntiforgeryReason });
        })
        .ProducesProblem(StatusCodes.Status400BadRequest);
}
