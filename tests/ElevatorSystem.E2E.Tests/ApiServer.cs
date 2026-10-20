using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace ElevatorSystem.E2E.Tests;

/// <summary>
/// The API as its own process, in single-process mode (the engine in process, in-memory messaging and storage), over
/// HTTPS with the development certificate, short car timings, demo scenarios on and one admin account for the tests.
/// </summary>
internal sealed class ApiServer : IAsyncDisposable
{
    public const string AdminEmail = "e2e.admin@elevator.test";

    /// <summary>A password for this throwaway, local-only test account; it exists only while the tests run.</summary>
    public const string AdminPassword = "e2e local test passphrase";

    private static readonly string AssemblyPath = Metadata("ApiAssemblyPath");
    private static readonly string ContentRoot = Metadata("ApiContentRoot");

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _output = new();

    private ApiServer(int port)
    {
        BaseAddress = new Uri($"https://127.0.0.1:{port}");
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetFullPath(ContentRoot),
        };
        start.ArgumentList.Add(Path.GetFullPath(AssemblyPath));

        // Inherit nothing that could change how the API runs, such as a coverage profiler.
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["ASPNETCORE_URLS"] = BaseAddress.ToString();
        start.Environment["Hosting__Mode"] = "SingleProcess";
        start.Environment["Elevator__TravelTimePerFloor"] = "00:00:00.400";
        start.Environment["Elevator__DoorOpenDuration"] = "00:00:03";
        start.Environment["Simulation__Enabled"] = "true";
        start.Environment["Staff__Accounts__0__Email"] = AdminEmail;
        start.Environment["Staff__Accounts__0__Password"] = AdminPassword;
        start.Environment["Staff__Accounts__0__Role"] = "Admin";
        start.Environment["Logging__LogLevel__Default"] = "Warning";

        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, line) => Record(line.Data);
        _process.ErrorDataReceived += (_, line) => Record(line.Data);
    }

    public Uri BaseAddress { get; }

    /// <summary>What the API wrote, for a failing test's message.</summary>
    public string Output => string.Join(Environment.NewLine, _output);

    public static async Task<ApiServer> StartAsync()
    {
        var server = new ApiServer(FreeTcpPort());
        server._process.Start();
        server._process.BeginOutputReadLine();
        server._process.BeginErrorReadLine();
        await server.WaitUntilReadyAsync();
        return server;
    }

    public async ValueTask DisposeAsync()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
        }

        _process.Dispose();
    }

    private async Task WaitUntilReadyAsync()
    {
        // The development certificate is not trusted on every machine; only this probe and the browser talk to it.
        using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator };
        using var http = new HttpClient(handler) { BaseAddress = BaseAddress, Timeout = TimeSpan.FromSeconds(2) };
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (DateTime.UtcNow < deadline)
        {
            if (_process.HasExited)
            {
                throw new InvalidOperationException($"The API exited with code {_process.ExitCode}:{Environment.NewLine}{Output}");
            }

            try
            {
                using var response = await http.GetAsync(new Uri("/api/v1/elevator", UriKind.Relative));
                if (response.StatusCode == HttpStatusCode.OK)
                {
                    return;
                }
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                // Not listening yet.
            }

            await Task.Delay(TimeSpan.FromMilliseconds(250));
        }

        throw new TimeoutException($"The API did not become ready at {BaseAddress}:{Environment.NewLine}{Output}");
    }

    private void Record(string? line)
    {
        if (line is null)
        {
            return;
        }

        _output.Enqueue(line);
        while (_output.Count > 400)
        {
            _output.TryDequeue(out _);
        }
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static string Metadata(string key) => typeof(ApiServer).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == key).Value!;
}
