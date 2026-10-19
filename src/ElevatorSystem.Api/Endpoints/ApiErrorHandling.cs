using System.Diagnostics;

namespace ElevatorSystem.Api.Endpoints;

/// <summary>
/// Errors under <c>/api</c> are RFC 9457 problem details carrying a <c>traceId</c>,
/// never the UI's HTML error pages and never exception details outside Development.
/// </summary>
internal static class ApiErrorHandling
{
    public const string TraceIdExtension = "traceId";

    /// <summary>Every API version lives under <c>/api</c>, so a request for an unknown version is an API error too.</summary>
    public const string ApiPrefix = "/api";

    public static bool IsApiRequest(HttpContext context) => context.Request.Path.StartsWithSegments(ApiPrefix);

    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services) =>
        services.AddProblemDetails(options => options.CustomizeProblemDetails = context =>
            context.ProblemDetails.Extensions.TryAdd(
                TraceIdExtension,
                Activity.Current?.Id ?? context.HttpContext.TraceIdentifier));

    /// <summary>
    /// Splits error handling: API requests get problem details for exceptions and for bodiless error
    /// statuses (404, 405, 415, malformed JSON); everything else keeps the UI's error and not-found pages.
    /// </summary>
    /// <remarks>
    /// The page handlers stay outermost because re-executing a page needs the top-level pipeline. The
    /// API handlers sit inside them and write their problem first, so the page handlers find the
    /// response already handled and leave it alone.
    /// </remarks>
    public static WebApplication UseErrorHandling(this WebApplication app)
    {
        var isDevelopment = app.Environment.IsDevelopment();

        if (!isDevelopment)
        {
            app.UseExceptionHandler("/Error", createScopeForErrors: true);
        }

        app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

        app.UseWhen(IsApiRequest, api =>
        {
            if (!isDevelopment)
            {
                api.UseExceptionHandler();
            }

            api.UseStatusCodePages();
        });

        return app;
    }
}
