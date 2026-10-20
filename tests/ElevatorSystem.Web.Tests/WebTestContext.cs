using ElevatorSystem.Web.Services;
using Microsoft.Extensions.Time.Testing;
using MudBlazor.Services;

namespace ElevatorSystem.Web.Tests;

/// <summary>
/// A bUnit context with the UI's real localisation, MudBlazor, state store and passenger journey, a clock stopped at
/// noon, and fakes for everything that would reach the network. JavaScript calls (MudBlazor's, localStorage) succeed
/// with default results.
/// </summary>
public abstract class WebTestContext : BunitContext
{
    protected WebTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton<TimeProvider>(Clock);
        Services.AddSingleton(Store);
        Services.AddSingleton<IElevatorApiClient>(Api);
        Services.AddSingleton<IElevatorHubClient>(Hub);
        Services.AddSingleton<IPassengerApiClient>(Passengers);
        Services.AddSingleton<PassengerJourney>();
        Services.AddSingleton<CulturePreference>();
        Services.AddSingleton<IStaffApiClient>(Staff);
        Services.AddSingleton<StaffSession>();
    }

    internal FakeTimeProvider Clock { get; } = new(TestData.Noon);

    internal ElevatorStateStore Store { get; } = new();

    internal FakeElevatorApiClient Api { get; } = new();

    internal FakeElevatorHubClient Hub { get; } = new();

    internal FakePassengerApiClient Passengers { get; } = new();

    internal FakeStaffApiClient Staff { get; } = new();
}
