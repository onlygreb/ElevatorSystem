using ElevatorSystem.Contracts;
using ElevatorSystem.Web.Services;

namespace ElevatorSystem.Web.Tests;

/// <summary>An API that answers from memory. Tests set what it returns.</summary>
internal sealed class FakeElevatorApiClient : IElevatorApiClient
{
    public ElevatorConfigurationDto Configuration { get; set; } = TestData.Configuration();

    public Exception? ConfigurationFailure { get; set; }

    public ElevatorStatusDto Status { get; set; } = TestData.Status();

    public int ConfigurationRequests { get; private set; }

    public Task<ElevatorConfigurationDto> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        ConfigurationRequests++;
        return ConfigurationFailure is null ? Task.FromResult(Configuration) : Task.FromException<ElevatorConfigurationDto>(ConfigurationFailure);
    }

    public Task<ElevatorStatusDto> GetStatusAsync(CancellationToken cancellationToken = default) => Task.FromResult(Status);
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

/// <summary>Passenger endpoints that answer from memory and record every step. Tests set what they return.</summary>
internal sealed class FakePassengerApiClient : IPassengerApiClient
{
    public Guid PassengerId { get; set; } = Guid.Parse("7d1c2a4e-0000-4000-8000-000000000001");

    /// <summary>What <see cref="GetAsync"/> answers, for a journey restored after a reload.</summary>
    public PassengerDto? Known { get; set; }

    /// <summary>The answer to the next step.</summary>
    public RequestOutcome NextOutcome { get; set; } = RequestOutcome.Accepted(Guid.NewGuid());

    /// <summary>When set, steps wait for it instead of answering at once, to hold a step "in flight".</summary>
    public TaskCompletionSource<RequestOutcome>? PendingAnswer { get; set; }

    public int Joins { get; private set; }

    public List<string> Steps { get; } = [];

    public async Task<(PassengerAcceptedResponse? Accepted, RequestOutcome Outcome)> JoinAsync(CancellationToken cancellationToken = default)
    {
        Joins++;
        var outcome = await Answer();
        return (outcome.IsAccepted ? new PassengerAcceptedResponse(PassengerId, outcome.RequestId!.Value) : null, outcome);
    }

    public Task<PassengerDto?> GetAsync(Guid passengerId, CancellationToken cancellationToken = default) =>
        Task.FromResult(Known is { } known && known.PassengerId == passengerId ? known : null);

    public Task<RequestOutcome> CallElevatorAsync(Guid passengerId, CallDirection direction, CancellationToken cancellationToken = default) =>
        Record($"call {direction}");

    public Task<RequestOutcome> BoardAsync(Guid passengerId, CancellationToken cancellationToken = default) => Record("board");

    public Task<RequestOutcome> ChooseFloorAsync(Guid passengerId, int floor, CancellationToken cancellationToken = default) =>
        Record($"floor {floor}");

    public Task<RequestOutcome> LeaveAsync(Guid passengerId, CancellationToken cancellationToken = default) => Record("leave");

    private Task<RequestOutcome> Record(string step)
    {
        Steps.Add(step);
        return Answer();
    }

    private Task<RequestOutcome> Answer() => PendingAnswer?.Task ?? Task.FromResult(NextOutcome);
}
