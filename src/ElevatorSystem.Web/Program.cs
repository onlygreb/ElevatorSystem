using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;
using Microsoft.AspNetCore.Components.WebAssembly.Hosting;
using MudBlazor.Services;

var builder = WebAssemblyHostBuilder.CreateDefault(args);

var baseAddress = new Uri(builder.HostEnvironment.BaseAddress);

builder.Services.AddLocalization();
builder.Services.AddMudServices();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<CulturePreference>();
builder.Services.AddSingleton<ElevatorStateStore>();
// Same origin as the page: the browser sends the session cookie itself and no token ever reaches this code (ADR-0006).
builder.Services.AddSingleton(_ => new HttpClient { BaseAddress = baseAddress });
builder.Services.AddSingleton<IElevatorApiClient, ElevatorApiClient>();
builder.Services.AddSingleton<IElevatorHubClient>(services =>
    ActivatorUtilities.CreateInstance<ElevatorHubClient>(services, new Uri(baseAddress, ElevatorHub.Path)));
builder.Services.AddSingleton<IStaffApiClient, StaffApiClient>();
builder.Services.AddSingleton<IPassengerApiClient, PassengerApiClient>();
builder.Services.AddSingleton<PassengerJourney>();
builder.Services.AddSingleton<StaffSession>();

var host = builder.Build();

// The culture must be set before the first render, so the matching satellite assembly is loaded with the app.
await host.Services.GetRequiredService<CulturePreference>().ApplyStoredAsync();

await host.RunAsync();
