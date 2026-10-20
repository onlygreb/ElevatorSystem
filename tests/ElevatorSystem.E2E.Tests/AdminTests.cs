using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ElevatorSystem.E2E.Tests;

/// <summary>The admin signs in with a second factor and starts a demo scenario (ADR-0022, ADR-0027).</summary>
[Collection(OneElevator.Name)]
public sealed class AdminTests(BrowserFixture fixture)
{
    [Fact]
    public async Task Admin_RegistersTheSecondFactor_StartsADemo_AndWatchesTheBuildingFill()
    {
        var page = await fixture.NewPageAsync();

        await SignInAsync(page, fixture);
        await page.GetByTestId("demo-MorningRush").ClickAsync();

        await Expect(page.GetByTestId("demo-message")).ToContainTextAsync("Morning rush started");
        await Expect(page.Locator(".storey .person--waiting, .storey .person--present, .cabin__riders .person--riding").First)
            .ToBeVisibleAsync(new() { Timeout = 30_000 });

        await page.GetByTestId("demo-stop").ClickAsync();
        await Expect(page.GetByTestId("demo-message")).ToContainTextAsync("The demo passengers are leaving.");
    }

    /// <summary>Signs the test admin in, registering the authenticator the first time.</summary>
    internal static async Task SignInAsync(IPage page, BrowserFixture fixture)
    {
        await page.GotoAsync("/admin");
        await page.GetByTestId("email").FillAsync(ApiServer.AdminEmail);
        await page.GetByTestId("password").FillAsync(ApiServer.AdminPassword);
        await page.GetByTestId("sign-in").ClickAsync();

        if (fixture.AdminSharedKey is { } key)
        {
            await page.GetByTestId("code").FillAsync(await FreshCodeAsync(key, fixture));
            await page.GetByTestId("sign-in").ClickAsync();
        }
        else
        {
            var sharedKey = await page.GetByTestId("shared-key").InnerTextAsync();
            fixture.AdminSharedKey = sharedKey;
            await page.GetByTestId("code").FillAsync(await FreshCodeAsync(sharedKey, fixture));
            await page.GetByTestId("enable").ClickAsync();
            await Expect(page.GetByTestId("recovery-codes")).ToBeVisibleAsync();
            await page.GetByTestId("codes-saved").ClickAsync();
        }

        await Expect(page.GetByTestId("account")).ToContainTextAsync(ApiServer.AdminEmail);
    }

    /// <summary>
    /// A code with a few seconds left in its window, so it is still valid when the server checks it, and never one already
    /// used: the server refuses a replayed code.
    /// </summary>
    private static async Task<string> FreshCodeAsync(string key, BrowserFixture fixture)
    {
        while (true)
        {
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var step = now / 30;
            if (now % 30 <= 25 && step != fixture.LastCodeStep)
            {
                fixture.LastCodeStep = step;
                return Totp.Code(key, DateTimeOffset.UtcNow);
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }
}
