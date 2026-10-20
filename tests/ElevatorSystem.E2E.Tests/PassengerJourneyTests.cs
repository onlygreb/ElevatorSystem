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
        await page.GotoAsync("/");

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
        await page.GotoAsync("/");
        await page.GetByTestId("arrive").ClickAsync();
        await Expect(Where(page)).ToHaveTextAsync("You are on floor 1");

        await page.ReloadAsync();

        await Expect(Where(page)).ToHaveTextAsync("You are on floor 1");
        await Expect(page.GetByTestId("arrive")).ToHaveCountAsync(0);
    }

    [Fact]
    public async Task DoorCountdown_ShowsWhileTheDoorsCanClose()
    {
        var page = await fixture.NewPageAsync();
        await page.GotoAsync("/");
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
        await page.GotoAsync("/");

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
