using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);

builder.Services.AddLocalization();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ElevatorStateStore>();
// Same origin as the page: the browser sends the session cookie itself and no token ever reaches this code (ADR-0006).
builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = baseAddress });
builder.Services.AddSingleton<IElevatorApiClient, ElevatorApiClient>();
builder.Services.AddSingleton<IElevatorHubClient>(services =>
    ActivatorUtilities.CreateInstance<ElevatorHubClient>(services, new Uri(baseAddress, ElevatorHub.Path)));

await builder.Build().RunAsync();
