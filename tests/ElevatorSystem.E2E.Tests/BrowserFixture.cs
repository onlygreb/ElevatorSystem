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
        Assertions.SetDefaultExpectTimeout(20_000);
        await WarmUpAsync();
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

    /// <summary>
    /// Loads the app and registers the test admin once before any test, with generous timeouts: the first load JIT-compiles
    /// the server and fetches the WebAssembly runtime, and the first sign-in hashes a password and warms the staff
    /// endpoints, which on a busy CI runner can take longer than a test should wait for a page.
    /// </summary>
    private async Task WarmUpAsync()
    {
        var page = await NewPageAsync();
        var console = new List<string>();
        page.Console += (_, message) => console.Add($"{message.Type}: {message.Text}");
        page.PageError += (_, error) => console.Add($"page error: {error}");
        page.RequestFailed += (_, request) => console.Add($"request failed: {request.Url} {request.Failure}");

        // A first load that stalls is reloaded rather than waited on forever; three tries before giving up.
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await page.GotoAsync("/", new() { Timeout = 60_000 });
                await page.GetByTestId("arrive").WaitForAsync(new() { Timeout = 60_000 });
                break;
            }
            catch (TimeoutException) when (attempt < 3)
            {
                console.Add($"-- attempt {attempt} timed out, reloading");
            }
            catch (TimeoutException exception)
            {
                var shown = await page.Locator("body").InnerTextAsync(new() { Timeout = 5_000 });
                throw new InvalidOperationException(
                    $"The app never loaded.{Environment.NewLine}The page showed:{Environment.NewLine}{shown}{Environment.NewLine}"
                    + $"The browser reported:{Environment.NewLine}{string.Join(Environment.NewLine, console)}{Environment.NewLine}"
                    + $"The API wrote:{Environment.NewLine}{ServerOutput}",
                    exception);
            }
        }

        await AdminTests.RegisterAsync(page, this, timeout: 120_000);
        await page.Context.CloseAsync();
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
        context.SetDefaultTimeout(30_000);
        return await context.NewPageAsync();
    }
}
