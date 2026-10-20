using Microsoft.Playwright;

[assembly: AssemblyFixture(typeof(ElevatorSystem.E2E.Tests.BrowserFixture))]

namespace ElevatorSystem.E2E.Tests;

/// <summary>The API and a headless Chromium, shared by every test; each test opens its own browser context.</summary>
public sealed class BrowserFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;
    private ApiServer? _server;
    private readonly System.Runtime.CompilerServices.ConditionalWeakTable<IPage, System.Collections.Concurrent.ConcurrentQueue<string>> _browserLogs = new();

    public IBrowser Browser { get; private set; } = default!;

    public Uri BaseAddress => _server!.BaseAddress;

    /// <summary>What the API wrote so far, for a failing test's message.</summary>
    public string ServerOutput => _server?.Output ?? string.Empty;

    /// <summary>The admin's authenticator key once the first sign-in registered it.</summary>
    public string? AdminSharedKey { get; set; }

    /// <summary>The 30-second step of the last authenticator code used, so none is used twice.</summary>
    public long LastCodeStep { get; set; } = -1;

    /// <summary>
    /// <c>ELEVATOR_E2E=defer</c> skips these tests: CI runs them in a step of their own, after the other suites, because
    /// containers starting in parallel change the network interfaces and Chromium aborts requests in flight when that
    /// happens (<c>net::ERR_NETWORK_CHANGED</c>).
    /// </summary>
    public static bool Deferred { get; } = Environment.GetEnvironmentVariable("ELEVATOR_E2E") == "defer";

    public async ValueTask InitializeAsync()
    {
        if (Deferred)
        {
            return;
        }

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
        await OpenAsync(page, "/", timeout: 60_000);
        await page.GetByTestId("arrive").WaitForAsync(new() { Timeout = 60_000 });
        await AdminTests.RegisterAsync(page, this, timeout: 120_000);
        await page.Context.CloseAsync();
    }

    /// <summary>
    /// Opens <paramref name="path"/> and waits until the app has rendered its layout. On a busy CI runner a fresh browser
    /// context sometimes never finishes starting the WebAssembly app; such a start is reloaded, up to three tries, and
    /// each stall is written to the page's log so a later failure shows it.
    /// </summary>
    public async Task OpenAsync(IPage page, string path, float timeout = 30_000)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await page.GotoAsync(path, new() { Timeout = timeout });
                await page.Locator("main").WaitForAsync(new() { Timeout = timeout });
                return;
            }
            catch (TimeoutException) when (attempt < 3)
            {
                Note(page, $"-- {path}: the app had not started after {timeout} ms (try {attempt}), reloading");
            }
            catch (TimeoutException exception)
            {
                throw await FailureAsync(page, $"The app never started at {path}.", exception);
            }
        }
    }

    /// <summary>A failure that says what the page showed, what the browser reported and what the API wrote.</summary>
    public async Task<Exception> FailureAsync(IPage page, string what, Exception inner)
    {
        string shown;
        try
        {
            shown = await page.Locator("body").InnerTextAsync(new() { Timeout = 2_000 });
        }
        catch (Exception exception) when (exception is TimeoutException or PlaywrightException)
        {
            shown = "(the page could not be read)";
        }

        return new InvalidOperationException(
            $"{what}{Environment.NewLine}{inner.Message}{Environment.NewLine}"
            + $"The page showed:{Environment.NewLine}{shown}{Environment.NewLine}"
            + $"The browser reported:{Environment.NewLine}{BrowserLog(page)}{Environment.NewLine}"
            + $"The API wrote:{Environment.NewLine}{ServerOutput}",
            inner);
    }

    private void Note(IPage page, string line)
    {
        if (_browserLogs.TryGetValue(page, out var log))
        {
            log.Enqueue(line);
        }
    }

    /// <summary>A fresh browser (its own cookies and storage), on the given culture.</summary>
    public async Task<IPage> NewPageAsync(string locale = "en-US")
    {
        if (Deferred)
        {
            Assert.Skip("Deferred: ELEVATOR_E2E=defer runs the browser tests in a step of their own.");
        }

        var context = await Browser.NewContextAsync(new()
        {
            BaseURL = BaseAddress.ToString(),
            IgnoreHTTPSErrors = true,
            Locale = locale,
            ViewportSize = new() { Width = 1280, Height = 900 },
        });
        context.SetDefaultTimeout(30_000);
        var page = await context.NewPageAsync();
        var log = new System.Collections.Concurrent.ConcurrentQueue<string>();
        page.Console += (_, message) => log.Enqueue($"{message.Type}: {message.Text}");
        page.PageError += (_, error) => log.Enqueue($"page error: {error}");
        page.RequestFailed += (_, request) => log.Enqueue($"request failed: {request.Method} {request.Url} {request.Failure}");
        page.Response += (_, response) =>
        {
            if (response.Status >= 400)
            {
                log.Enqueue($"{response.Status} {response.Request.Method} {response.Url}");
            }
        };
        _browserLogs.AddOrUpdate(page, log);
        return page;
    }

    /// <summary>What the browser reported for <paramref name="page"/>: console messages, page errors, failed and refused requests.</summary>
    public string BrowserLog(IPage page) =>
        _browserLogs.TryGetValue(page, out var log) ? string.Join(Environment.NewLine, log) : string.Empty;
}
