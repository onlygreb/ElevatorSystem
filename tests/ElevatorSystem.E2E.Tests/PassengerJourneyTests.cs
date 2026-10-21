using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ElevatorSystem.E2E.Tests;

/// <summary>A passenger rides the elevator in the browser (ADR-0026), against the running system.</summary>
[Collection(OneElevator.Name)]
public sealed class PassengerJourneyTests(BrowserFixture fixture)
{
    [Fact]
    public async Task Passenger_EntersTheBuilding_RidesToAFloor_AndWalksOut()
    {
        var page = await fixture.NewPageAsync();
        await fixture.OpenAsync(page, "/");

        await Expect(Where(page)).ToHaveTextAsync("You are outside the building");
        await page.GetByTestId("arrive").ClickAsync();
        await Expect(Where(page)).ToHaveTextAsync("You are on floor 1");
        await Expect(page.Locator(".storey[data-floor='1'] [data-testid=you-on-floor]")).ToBeVisibleAsync();

        await BoardAsync(page);
        await Expect(Where(page)).ToHaveTextAsync("You are in the elevator, at floor 1");
        await page.Locator(".car-button[data-floor='3']").ClickAsync();
        await Expect(page.Locator(".car-button[data-floor='3']")).ToHaveAttributeAsync("aria-pressed", "true");

        await Expect(Where(page)).ToHaveTextAsync("You are in the elevator, at floor 3");
        await Expect(page.GetByTestId("leave")).ToBeEnabledAsync();
        await page.GetByTestId("leave").ClickAsync();

        await Expect(Where(page)).ToHaveTextAsync("You are on floor 3");
        await Expect(page.Locator(".storey[data-floor='3'] [data-testid=you-on-floor]")).ToBeVisibleAsync();
        await Expect(page.Locator(".activity__item--mine").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task Reload_PicksUpTheJourney()
    {
        var page = await fixture.NewPageAsync();
        await fixture.OpenAsync(page, "/");
        await page.GetByTestId("arrive").ClickAsync();
        await Expect(Where(page)).ToHaveTextAsync("You are on floor 1");

        await page.ReloadAsync();

        await Expect(Where(page)).ToHaveTextAsync("You are on floor 1");
        await Expect(page.GetByTestId("arrive")).ToHaveCountAsync(0);

        // The reload also said "this tab is going away"; coming back within the grace kept the passenger (ADR-0036).
        await page.WaitForTimeoutAsync((float)(ApiServer.DepartureGrace + TimeSpan.FromSeconds(2)).TotalMilliseconds);
        var id = await page.EvaluateAsync<string>("sessionStorage.getItem('elevator.passenger')");
        (await page.APIRequest.GetAsync($"api/v1/passengers/{id}")).Status.ShouldBe(200);
    }

    [Fact]
    public async Task ClosingATab_TakesItsPassengerOutOfTheBuilding()
    {
        var page = await fixture.NewPageAsync();
        await fixture.OpenAsync(page, "/");
        await page.GetByTestId("arrive").ClickAsync();
        await Expect(Where(page)).ToHaveTextAsync("You are on floor 1");
        var id = await page.EvaluateAsync<string>("sessionStorage.getItem('elevator.passenger')");
        var browser = page.Context;

        await page.CloseAsync();

        // Asking within the grace would call the departure off, as a reloaded tab does: wait it out first.
        var observer = await browser.NewPageAsync();
        await observer.WaitForTimeoutAsync((float)(ApiServer.DepartureGrace + TimeSpan.FromSeconds(1)).TotalMilliseconds);
        var status = 0;
        for (var attempt = 0; attempt < 20 && status != 404; attempt++)
        {
            status = (await observer.APIRequest.GetAsync($"api/v1/passengers/{id}")).Status;
            await observer.WaitForTimeoutAsync(250);
        }

        status.ShouldBe(404, "the passenger of a closed tab left the building");
    }

    [Fact]
    public async Task EveryTab_IsAPassengerOfItsOwn()
    {
        var first = await fixture.NewPageAsync();
        await fixture.OpenAsync(first, "/");
        await first.GetByTestId("arrive").ClickAsync();
        await Expect(Where(first)).ToHaveTextAsync("You are on floor 1");

        // Another tab of the same browser: a second person arriving, not the first one again (ADR-0035).
        var second = await first.Context.NewPageAsync();
        await fixture.OpenAsync(second, "/");

        await Expect(second.GetByTestId("arrive")).ToBeVisibleAsync();
        await second.GetByTestId("arrive").ClickAsync();
        await Expect(Where(second)).ToHaveTextAsync("You are on floor 1");
        var ids = await Task.WhenAll(
            first.EvaluateAsync<string>("sessionStorage.getItem('elevator.passenger')"),
            second.EvaluateAsync<string>("sessionStorage.getItem('elevator.passenger')"));
        ids[0].ShouldNotBe(ids[1]);
    }

    [Fact]
    public async Task DoorCountdown_ShowsWhileTheDoorsCanClose()
    {
        var page = await fixture.NewPageAsync();
        await fixture.OpenAsync(page, "/");
        await page.GetByTestId("arrive").ClickAsync();

        await BoardAsync(page);
        await page.Locator(".car-button[data-floor='2']").ClickAsync();

        // A destination starts the countdown on the open doors (ADR-0025) before the car leaves.
        var countdownOrMoving = page.GetByTestId("door-countdown").First.Or(page.Locator(".cabin--moving"));
        await Expect(countdownOrMoving).ToBeVisibleAsync();
        await Expect(Where(page)).ToHaveTextAsync("You are in the elevator, at floor 2");
    }

    [Fact]
    public async Task Portuguese_IsOfferedAndRemembered()
    {
        var page = await fixture.NewPageAsync();
        await fixture.OpenAsync(page, "/");

        await page.Locator(".culture-picker__option[data-culture='pt-BR']").ClickAsync();

        await Expect(Where(page)).ToHaveTextAsync("Você está fora do prédio");
        await Expect(page.Locator("html")).ToHaveAttributeAsync("lang", "pt-BR");
        await page.ReloadAsync();
        await Expect(page.GetByTestId("arrive")).ToContainTextAsync("Entrar no prédio");
    }

    private static ILocator Where(IPage page) => page.GetByTestId("journey-where");

    /// <summary>Walks into the car on the passenger's floor, calling it first if it is elsewhere or its doors are shut.</summary>
    internal static async Task BoardAsync(IPage page)
    {
        var enter = page.GetByTestId("enter");
        var callUp = page.GetByTestId("call-up");
        await Expect(enter.Or(callUp)).ToBeVisibleAsync();
        if (await callUp.IsVisibleAsync())
        {
            await callUp.ClickAsync();
        }

        await Expect(enter).ToBeVisibleAsync(new() { Timeout = 30_000 });
        await enter.ClickAsync();
        await Expect(page.Locator(".car-button")).ToHaveCountAsync(10);
    }
}
