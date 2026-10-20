using Microsoft.Playwright;
using static Microsoft.Playwright.Assertions;

namespace ElevatorSystem.E2E.Tests;

/// <summary>The admin signs in with a second factor and starts a demo scenario (ADR-0022, ADR-0027).</summary>
[Collection(OneElevator.Name)]
public sealed class AdminTests(BrowserFixture fixture)
{
    [Fact]
    public async Task Admin_SignsInWithTheSecondFactor_StartsADemo_AndWatchesTheBuildingFill()
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

    [Fact]
    public async Task Admin_StartsAHugeLoad_SeesTheBuildingFastForwarded_AndStopsIt()
    {
        var page = await fixture.NewPageAsync();

        await SignInAsync(page, fixture);
        await page.GetByTestId("demo-HugeLoad").ClickAsync();

        await Expect(page.GetByTestId("demo-message")).ToContainTextAsync("Huge load started");
        await Expect(page.GetByTestId("status-speed")).ToContainTextAsync("Fast-forward 8×");

        await page.GetByTestId("demo-stop").ClickAsync();
        // The car first serves, still fast-forwarded, the calls the crowd left behind.
        await Expect(page.GetByTestId("status-speed")).ToHaveCountAsync(0, new() { Timeout = 60_000 });
    }

    /// <summary>
    /// Registers the test admin's authenticator: the first sign-in with a password asks for it, then shows recovery
    /// codes. The fixture does this once, before any test, with a generous timeout.
    /// </summary>
    internal static async Task RegisterAsync(IPage page, BrowserFixture fixture, float timeout)
    {
        await fixture.OpenAsync(page, "/admin", timeout);
        await page.GetByTestId("email").FillAsync(ApiServer.AdminEmail, new() { Timeout = timeout });
        await page.GetByTestId("password").FillAsync(ApiServer.AdminPassword);
        await page.GetByTestId("sign-in").ClickAsync();

        var sharedKey = await Explained(page, fixture, () => page.GetByTestId("shared-key").InnerTextAsync(new() { Timeout = timeout }));
        fixture.AdminSharedKey = sharedKey;
        await page.GetByTestId("code").FillAsync(await FreshCodeAsync(sharedKey, fixture));
        await page.GetByTestId("enable").ClickAsync();
        await Explained(page, fixture, () => page.GetByTestId("recovery-codes").WaitForAsync(new() { Timeout = timeout }));
        await page.GetByTestId("codes-saved").ClickAsync();
        await Explained(page, fixture, () => Expect(page.GetByTestId("account")).ToContainTextAsync(ApiServer.AdminEmail, new() { Timeout = timeout }));
    }

    /// <summary>Signs the registered test admin in with their password and a fresh authenticator code.</summary>
    internal static async Task SignInAsync(IPage page, BrowserFixture fixture)
    {
        var key = fixture.AdminSharedKey ?? throw new InvalidOperationException("The fixture registers the admin before any test.");
        await fixture.OpenAsync(page, "/admin");
        await Explained(page, fixture, () => page.GetByTestId("email").FillAsync(ApiServer.AdminEmail));
        await page.GetByTestId("password").FillAsync(ApiServer.AdminPassword);
        await page.GetByTestId("sign-in").ClickAsync();

        await Explained(page, fixture, () => page.GetByTestId("code").WaitForAsync());
        await page.GetByTestId("code").FillAsync(await FreshCodeAsync(key, fixture));
        await page.GetByTestId("sign-in").ClickAsync();
        await Explained(page, fixture, () => Expect(page.GetByTestId("account")).ToContainTextAsync(ApiServer.AdminEmail));
    }

    /// <summary>Runs a step; if it times out, fails with what the page and the API said, not just the selector.</summary>
    private static Task<bool> Explained(IPage page, BrowserFixture fixture, Func<Task> step) =>
        Explained(page, fixture, async () =>
        {
            await step();
            return true;
        });

    private static async Task<T> Explained<T>(IPage page, BrowserFixture fixture, Func<Task<T>> step)
    {
        try
        {
            return await step();
        }
        catch (Exception exception) when (exception is TimeoutException or PlaywrightException)
        {
            throw await fixture.FailureAsync(page, "A sign-in step did not finish.", exception);
        }
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
