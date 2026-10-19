using System.Text.Json.Serialization;
using ElevatorSystem.Api;
using ElevatorSystem.Api.Components;
using ElevatorSystem.Api.Endpoints;
using ElevatorSystem.Api.Hosting;
using ElevatorSystem.Api.RealTime;
using ElevatorSystem.Api.Security;
using Scalar.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

builder.AddElevator();
builder.AddElevatorRealTime();
builder.AddApiRateLimiting();

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
app.UseRateLimiter();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapDefaultEndpoints();
app.MapElevatorEndpoints();
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
