using ElevatorSystem.Web.Services;
using MudBlazor.Services;

namespace ElevatorSystem.Web.Tests;

/// <summary>
/// A bUnit context with the UI's real localisation, MudBlazor and state store, and fakes for everything that would
/// reach the network. JavaScript calls (MudBlazor's) succeed with default results.
/// </summary>
public abstract class WebTestContext : BunitContext
{
    protected WebTestContext()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        Services.AddLocalization();
        Services.AddMudServices();
        Services.AddSingleton(Store);
        Services.AddSingleton<IElevatorApiClient>(Api);
        Services.AddSingleton<IElevatorHubClient>(Hub);
        Services.AddSingleton<CulturePreference>();
        Services.AddSingleton<IStaffApiClient>(Staff);
        Services.AddSingleton<StaffSession>();
    }

    internal ElevatorStateStore Store { get; } = new();

    internal FakeElevatorApiClient Api { get; } = new();

    internal FakeElevatorHubClient Hub { get; } = new();

    internal FakeStaffApiClient Staff { get; } = new();
}
