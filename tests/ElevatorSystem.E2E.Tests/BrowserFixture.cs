using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(ElevatorSystem.E2E.Tests.BrowserFixture))]

namespace ElevatorSystem.E2E.Tests;

/// <summary>The API and a headless Chromium, shared by every test; each test opens its own browser context.</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private ApiServer? _server;

    public IBrowser Browser { get; private set; } = default!;

    public Uri BaseAddress => _server!.BaseAddress;

    /// <summary>What the API wrote so far, for a failing test's message.</summary>
    public string ServerOutput => _server?.Output ?? string.Empty;

    /// <summary>The admin's authenticator key once the first sign-in registered it.</summary>
    public string? AdminSharedKey { get; set; }

    /// <summary>The 30-second step of the last authenticator code used, so none is used twice.</summary>
    public long LastCodeStep { get; set; } = -1;

    public async ValueTask InitializeAsync()
    {
        // Downloads Chromium the first time only; a no-op when it is already installed.
        var installed = Microsoft.Playwright.Program.Main(["install", "chromium"]);
        if (installed != 0)
        {
            throw new InvalidOperationException($"Installing Chromium for Playwright failed with exit code {installed}.");
        }

        _server = await ApiServer.StartAsync();
        _playwright = await Playwright.CreateAsync();
        Browser = await _playwright.Chromium.LaunchAsync(new() { Headless = true });
    }

    public async ValueTask DisposeAsync()
    {
        if (Browser is not null)
        {
            await Browser.DisposeAsync();
        }

        _playwright?.Dispose();
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    /// <summary>A fresh browser (its own cookies and storage), on the given culture.</summary>
    public async Task<IPage> NewPageAsync(string locale = "en-US")
    {
        var context = await Browser.NewContextAsync(new()
        {
            BaseURL = BaseAddress.ToString(),
            IgnoreHTTPSErrors = true,
            Locale = locale,
            ViewportSize = new() { Width = 1280, Height = 900 },
        });
        context.SetDefaultTimeout(20_000);
        return await context.NewPageAsync();
    }
}
