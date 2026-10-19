namespace ElevatorSystem.Api.Security;

/// <summary>
/// Browser hardening headers on every response: API calls, pages, static files, errors and rate-limit
/// refusals alike. They are written just before the response starts, so error handlers that clear the
/// response cannot remove them.
/// </summary>
internal static class SecurityHeaders
{
    /// <summary>
    /// Same-origin only. WebAssembly needs <c>'wasm-unsafe-eval'</c> (ADR-0006); inline style attributes are
    /// allowed for the component library, inline scripts and every external origin are not.
    /// </summary>
    public const string ContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; "
        + "base-uri 'self'; form-action 'self'";

    /// <summary>
    /// The API reference UI (Development only) configures itself with an inline script, so its pages get
    /// that one relaxation; everything else is identical to <see cref="ContentSecurityPolicy"/>.
    /// </summary>
    public const string ApiReferenceContentSecurityPolicy =
        "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; "
        + "img-src 'self' data:; font-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; "
        + "base-uri 'self'; form-action 'self'";

    /// <summary>The application uses none of these browser features, so none may be used by anything it loads.</summary>
    public const string PermissionsPolicy =
        "accelerometer=(), autoplay=(), browsing-topics=(), camera=(), display-capture=(), encrypted-media=(), "
        + "fullscreen=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), midi=(), payment=(), "
        + "picture-in-picture=(), publickey-credentials-get=(), screen-wake-lock=(), usb=(), xr-spatial-tracking=()";

    public const string ApiReferencePath = "/scalar";

    public static IApplicationBuilder UseSecurityHeaders(this IApplicationBuilder app, bool isDevelopment) =>
        app.Use((context, next) =>
        {
            context.Response.OnStarting(() =>
            {
                var headers = context.Response.Headers;
                headers.ContentSecurityPolicy = isDevelopment && context.Request.Path.StartsWithSegments(ApiReferencePath)
                    ? ApiReferenceContentSecurityPolicy
                    : ContentSecurityPolicy;
                headers.XContentTypeOptions = "nosniff";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Permissions-Policy"] = PermissionsPolicy;
                headers["Cross-Origin-Opener-Policy"] = "same-origin";
                return Task.CompletedTask;
            });

            return next(context);
        });
}
