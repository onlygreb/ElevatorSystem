using ElevatorSystem.Engine;

var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);
builder.AddElevatorEngineHost();

var app = builder.Build();

// The engine serves no business endpoints, only the orchestrator's health probes and its role.
app.MapDefaultEndpoints();
app.MapEngineRole();

await app.RunAsync();
