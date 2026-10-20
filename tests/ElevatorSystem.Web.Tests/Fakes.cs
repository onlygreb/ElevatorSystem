using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests;

/// <summary>An API that answers from memory and records every call. Tests set what it returns.</summary>
internal sealed class FakeElevatorApiClient : IElevatorApiClient
{
    public ElevatorConfigurationDto Configuration { get; set; } = TestData.Configuration();

    public Exception? ConfigurationFailure { get; set; }

    public ElevatorStatusDto Status { get; set; } = TestData.Status();

    /// <summary>The answer to the next hall or car call.</summary>
    public RequestOutcome NextOutcome { get; set; } = RequestOutcome.Accepted(Guid.NewGuid());

    /// <summary>When set, calls wait for it instead of answering at once, to hold a request "in flight".</summary>
    public TaskCompletionSource<RequestOutcome>? PendingAnswer { get; set; }

    public int ConfigurationRequests { get; private set; }

    public List<HallCallRequest> HallCalls { get; } = [];

    public List<int> CarCalls { get; } = [];

    public Task<ElevatorConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ConfigurationRequests++;
        return ConfigurationFailure is null ? Task.FromResult(Configuration) : Task.FromException<ElevatorConfigurationDto>(ConfigurationFailure);
    }

    public Task<ElevatorStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);

    public Task<RequestOutcome> RequestElevatorAsync(int floor, CallDirection direction, CancellationToken cancellationToken = default)
    {
        HallCalls.Add(new HallCallRequest(floor, direction));
        return Answer();
    }

    public Task<RequestOutcome> RequestDestinationAsync(int floor, CancellationToken cancellationToken = default)
    {
        CarCalls.Add(floor);
        return Answer();
    }

    private Task<RequestOutcome> Answer() => PendingAnswer?.Task ?? Task.FromResult(NextOutcome);
}

/// <summary>A hub that never connects. Tests play the server by calling the store directly.</summary>
internal sealed class FakeElevatorHubClient : IElevatorHubClient
{
    public int StartCalls { get; private set; }

    public int RestartCalls { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        StartCalls++;
        return Task.CompletedTask;
    }

    public Task RestartAsync(CancellationToken cancellationToken = default)
    {
        RestartCalls++;
        return Task.CompletedTask;
    }
}
