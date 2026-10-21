using System.Text.Json.Serialization;
using ElevatorSystem.Api;
using ElevatorSystem.Api.Components;
using ElevatorSystem.Api.Endpoints;
using ElevatorSystem.Api.Hosting;
using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Api.Security;
using ElevatorSystem.Api.Staff;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Before Kestrel loads it: Docker Compose may ask for a certificate to be made on first start (ADR-0034).
var createdCertificate = SelfSignedCertificate.EnsureExists(builder.Configuration, TimeProvider.System);

builder.AddServiceDefaults();
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

// Real time first: hosted services start in registration order, so the update subscription is in place before an
// in-process engine (single-process mode) publishes its first status, which would otherwise be lost until the next refresh.
builder.AddElevatorRealTime();
builder.AddElevator();
builder.AddApiRateLimiting();
builder.AddStaff();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    ContractsJson.Apply(options.SerializerOptions);
    // A request that leaves out a field (say, the direction) is refused rather than defaulted.
    options.SerializerOptions.RespectRequiredConstructorParameters = true;
    // Numbers must be JSON numbers: "floor": "3" is refused, and the OpenAPI document says "integer".
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.Services.AddApiProblemDetails();
builder.Services.AddOpenApi();
builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});
builder.Services.AddRazorComponents()
    .AddInteractiveWebAssemblyComponents();

var app = builder.Build();

if (createdCertificate is not null)
{
    ApiLog.SelfSignedCertificateCreated(app.Logger, createdCertificate);
}

// First, so every response carries the headers, whichever middleware produces it.
app.UseSecurityHeaders(app.Environment.IsDevelopment());

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseHsts();
}

app.UseErrorHandling();
app.UseHttpsRedirection();
app.UseAuthentication();
// After authentication, so staff requests are counted per account.
app.UseRateLimiter();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapElevatorEndpoints();
app.MapStaffAuthEndpoints();
app.MapAdminEndpoints();
app.MapPassengerEndpoints();
app.MapElevatorHub();
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(ElevatorSystem.Web._Imports).Assembly);

// The API description is a reconnaissance aid, so it exists only in Development.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.MapScalarApiReference();
}

await app.RunAsync();
