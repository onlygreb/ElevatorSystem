var builder = WebApplication.CreateBuilder(args);

builder.AddServiceDefaults();
builder.WebHost.ConfigureKestrel(options => options.AddServerHeader = false);

var app = builder.Build();

// The engine serves no business endpoints, only the orchestrator's health probes.
app.MapDefaultEndpoints();

await app.RunAsync();
