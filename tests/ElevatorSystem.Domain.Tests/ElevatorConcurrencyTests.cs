using static ElevatorSystem.Domain.Tests.TestElevators;

namespace ElevatorSystem.Domain.Tests;

/// <summary>
/// Hammers one elevator from many threads at once. Every operation is released together by a gate,
/// so they genuinely race; the assertions prove each transition is atomic.
/// </summary>
public sealed class ElevatorConcurrencyTests
{
    [Fact]
    public async Task AddRequest_FromManyThreads_NeitherLosesNorDuplicatesARequest()
    {
        var elevator = At(1, TimeProvider.System);
        var calls = AllValidCalls(Building.Default);
        const int RequestsPerCall = 200;
        var requests = calls
            .SelectMany(call => Enumerable.Range(0, RequestsPerCall).Select(_ => Request(call, TimeProvider.System)))
            .OrderBy(_ => Random.Shared.Next())
            .ToArray();

        var results = await RunTogether(requests, elevator.AddRequest);

        results.Count(result => result.Outcome == AddRequestOutcome.Queued).ShouldBe(calls.Count, "each distinct call is queued exactly once");
        results.Count(result => result.Outcome == AddRequestOutcome.Merged).ShouldBe(requests.Length - calls.Count);
        var pending = elevator.Snapshot().PendingCalls;
        pending.Select(call => call.Call).ShouldBeUnique();
        pending.Sum(call => call.RequestCount).ShouldBe(requests.Length, "every request is counted once");
    }

    [Fact]
    public async Task MoveUp_FromManyThreads_StopsExactlyAtTheTopFloor()
    {
        var elevator = At(1);

        var results = await RunTogether(Enumerable.Range(0, 200).ToArray(), _ => elevator.MoveUp());

        results.Count(result => result.Succeeded).ShouldBe(9);
        results.Where(result => !result.Succeeded).ShouldAllBe(result => result.Error == TransitionError.AtTopFloor);
        elevator.CurrentFloor.ShouldBe(new Floor(10));
    }

    [Fact]
    public async Task MixedTransitions_FromManyThreads_AccountForEveryFloorChange()
    {
        var elevator = At(5, TimeProvider.System);
        var commands = Enumerable.Range(0, 4000).Select(_ => (ElevatorCommand)Random.Shared.Next(4)).ToArray();

        var outcomes = await RunTogether(commands, command => (command, result: Apply(elevator, command)));

        var ups = outcomes.Count(outcome => outcome.command == ElevatorCommand.MoveUp && outcome.result.Succeeded);
        var downs = outcomes.Count(outcome => outcome.command == ElevatorCommand.MoveDown && outcome.result.Succeeded);
        elevator.CurrentFloor.ShouldBe(new Floor(5 + ups - downs), "each successful move changed the floor by exactly one");
        Building.Default.Contains(elevator.CurrentFloor).ShouldBeTrue();
    }

    private static async Task<TResult[]> RunTogether<TInput, TResult>(IReadOnlyList<TInput> inputs, Func<TInput, TResult> operation)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = inputs
            .Select(input => Task.Run(async () =>
            {
                await gate.Task;
                return operation(input);
            }))
            .ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private static TransitionResult Apply(Elevator elevator, ElevatorCommand command) => command switch
    {
        ElevatorCommand.MoveUp => elevator.MoveUp(),
        ElevatorCommand.MoveDown => elevator.MoveDown(),
        ElevatorCommand.OpenDoor => elevator.OpenDoor(),
        _ => elevator.CloseDoor(),
    };
}
