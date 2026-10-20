using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;

namespace ElevatorSystem.Integration.Tests;

/// <summary>
/// The engine host running as its own process, configured like the AppHost configures it but with short
/// timings. <see cref="Kill"/> ends it the way a crash does: no shutdown code runs, the operating system
/// closes its broker and database connections.
/// </summary>
public sealed class EngineProcess : IAsyncDisposable
{
    private static readonly string AssemblyPath = typeof(EngineProcess).Assembly
        .GetCustomAttributes<AssemblyMetadataAttribute>()
        .Single(attribute => attribute.Key == "EngineAssemblyPath").Value!;

    private readonly Process _process;
    private readonly ConcurrentQueue<string> _output = new();
    private readonly HttpClient _http;

    private EngineProcess(string name, TestEnvironment environment, IReadOnlyDictionary<string, string> settings)
    {
        Name = name;
        var port = FreeTcpPort();
        _http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(2) };

        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(Path.GetFullPath(AssemblyPath));

        // Inherit nothing that could change how the engine runs, such as a coverage profiler.
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("CORECLR_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("COR_", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("DOTNET_STARTUP_HOOKS", StringComparison.OrdinalIgnoreCase)
            || key.StartsWith("ASPNETCORE_", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            start.Environment.Remove(key);
        }

        var environmentVariables = new Dictionary<string, string>
        {
            ["ASPNETCORE_ENVIRONMENT"] = "Development",
            ["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}",
            ["ConnectionStrings__messaging"] = environment.BrokerUri.ToString(),
            ["ConnectionStrings__elevatordb"] = environment.DatabaseConnectionString,
            ["Messaging__ClientName"] = name,
            ["Messaging__RecoveryInterval"] = "00:00:01",
            ["Messaging__ConnectTimeout"] = "00:00:02",
            ["Persistence__LeaderCheckInterval"] = "00:00:00.200",
            ["Persistence__LeaderAcquireRetryInterval"] = "00:00:00.200",
            ["Engine__StatusRefreshInterval"] = "00:00:01",
            ["Engine__StoreRetryInterval"] = "00:00:00.200",
            ["Elevator__TravelTimePerFloor"] = Timings.TravelTimePerFloor,
            ["Elevator__DoorOpenDuration"] = Timings.DoorOpenDuration,
            // Generous, so an engine starved of CPU on a shared runner is never mistaken for a stuck car.
            ["Elevator__StuckTolerance"] = "50",
            ["Logging__Console__FormatterName"] = "simple",
            ["Logging__Console__FormatterOptions__SingleLine"] = "true",
            ["Logging__Console__FormatterOptions__TimestampFormat"] = "HH:mm:ss.fff ",
        };
        foreach (var (key, value) in settings)
        {
            environmentVariables[key.Replace(":", "__", StringComparison.Ordinal)] = value;
        }

        foreach (var (key, value) in environmentVariables)
        {
            start.Environment[key] = value;
        }

        _process = new Process { StartInfo = start, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, line) => Record(line.Data);
        _process.ErrorDataReceived += (_, line) => Record(line.Data);
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
    }

    public string Name { get; }

    public bool HasExited => _process.HasExited;

    /// <summary>Every line the engine wrote so far.</summary>
    public IReadOnlyList<string> Output => [.. _output];

    /// <summary>Starts an engine; <paramref name="settings"/> override configuration keys (for example <c>Elevator:TravelTimePerFloor</c>).</summary>
    public static EngineProcess Start(string name, TestEnvironment environment, IReadOnlyDictionary<string, string>? settings = null) =>
        new(name, environment, settings ?? new Dictionary<string, string>());

    /// <summary>The role the engine reports, or <see langword="null"/> while it does not answer.</summary>
    public async Task<string?> RoleAsync()
    {
        try
        {
            return await _http.GetStringAsync(new Uri("/health/role", UriKind.Relative), TestContext.Current.CancellationToken);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    public Task WaitForRoleAsync(string role, TimeSpan? timeout = null) =>
        Eventually.HoldsAsync(async () => !HasExited && await RoleAsync() == role, $"{Name} to be {role}", timeout ?? TimeSpan.FromSeconds(60));

    /// <summary>Ends the process at once, as a crash or a power cut would.</summary>
    public void Kill()
    {
        if (!_process.HasExited)
        {
            _process.Kill(entireProcessTree: true);
            _process.WaitForExit();
        }
    }

    public async ValueTask DisposeAsync()
    {
        Kill();
        var output = TestContext.Current.TestOutputHelper;
        if (output is not null)
        {
            foreach (var line in _output)
            {
                output.WriteLine($"[{Name}] {line}");
            }
        }

        _process.Dispose();
        _http.Dispose();
        await Task.CompletedTask;
    }

    private void Record(string? line)
    {
        if (line is not null)
        {
            _output.Enqueue(line);
        }
    }

    private static int FreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

/// <summary>Short elevator timings shared by the engines and the API in these tests.</summary>
public static class Timings
{
    public const string TravelTimePerFloor = "00:00:00.200";

    public const string DoorOpenDuration = "00:00:00.300";

    /// <summary>For tests that need calls to stay pending for a while.</summary>
    public const string SlowTravelTimePerFloor = "00:00:01";
}
