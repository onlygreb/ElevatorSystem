using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ElevatorSystem.E2E.Tests;

/// <summary>
/// Refreshes the README's screenshots from the running system. Skipped unless <c>ELEVATOR_SCREENSHOTS</c> names a folder
/// (an absolute path, since a relative one resolves against the test's output folder):
/// <c>ELEVATOR_SCREENSHOTS="$PWD/docs/images" dotnet test --project tests/ElevatorSystem.E2E.Tests</c>.
/// </summary>
[Collection(OneElevator.Name)]
public sealed class ReadmeScreenshots(BrowserFixture fixture)
{
    private static readonly string? Folder = Environment.GetEnvironmentVariable("ELEVATOR_SCREENSHOTS");

    [Fact]
    public async Task Capture_TheAdminDashboard_ThePassengerJourney_AndThePhoneView()
    {
        Assert.SkipWhen(string.IsNullOrWhiteSpace(Folder), "Set ELEVATOR_SCREENSHOTS to a folder to refresh the README's screenshots.");
        var folder = Path.GetFullPath(Folder!);
        Directory.CreateDirectory(folder);

        // The admin starts a busy day, so the building is full of people in every picture.
        var admin = await fixture.NewPageAsync(viewport: new() { Width = 1440, Height = 1180 }, colorScheme: ColorScheme.Dark);
        await AdminTests.SignInAsync(admin, fixture);
        await admin.GetByTestId("demo-BusyDay").ClickAsync();
        await Expect(admin.GetByTestId("demo-message")).ToContainTextAsync("Busy day started");
        await admin.WaitForTimeoutAsync(12_000);
        await admin.EvaluateAsync("window.scrollTo(0, 0)");
        await admin.ScreenshotAsync(new() { Path = Path.Combine(folder, "admin-dashboard.png") });

        // A passenger rides among them: in the car, a floor chosen, the doors counting down.
        var passenger = await fixture.NewPageAsync(viewport: new() { Width = 1440, Height = 1000 });
        await fixture.OpenAsync(passenger, "/");
        await passenger.GetByTestId("arrive").ClickAsync();
        await PassengerJourneyTests.BoardAsync(passenger);
        await passenger.Locator(".car-button[data-floor='8']").ClickAsync();
        await Expect(passenger.Locator(".car-button[data-floor='8']")).ToHaveAttributeAsync("aria-pressed", "true");
        // While the doors still count down: a picture taken as the car leaves would catch it mid-glide.
        await Expect(passenger.GetByTestId("door-countdown").First).ToBeVisibleAsync();
        await passenger.WaitForTimeoutAsync(200);
        await passenger.ScreenshotAsync(new() { Path = Path.Combine(folder, "passenger-journey.png") });

        // The same journey on a phone, in Portuguese.
        var phone = await fixture.NewPageAsync("pt-BR", new() { Width = 390, Height = 844 }, ColorScheme.Dark);
        await fixture.OpenAsync(phone, "/");
        await phone.Locator(".culture-picker__option[data-culture='pt-BR']").ClickAsync();
        await Expect(phone.GetByTestId("arrive")).ToContainTextAsync("Entrar no prédio");
        await phone.GetByTestId("arrive").ClickAsync();
        await Expect(phone.GetByTestId("journey-where")).ToHaveTextAsync("Você está no andar 1");
        await phone.ScreenshotAsync(new() { Path = Path.Combine(folder, "passenger-phone.png") });

        await admin.GetByTestId("demo-stop").ClickAsync();
    }
}
