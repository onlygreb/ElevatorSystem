using ElevatorSystem.Application.Simulation;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using ElevatorSystem.Domain.Scheduling;
using Microsoft.Extensions.Logging.Abstractions;

namespace ElevatorSystem.Application.Tests;

/// <summary>
/// Real time, real threads: thousands of requests released at once while the car is mid-trip.
/// Proves nothing is lost or duplicated and that assignment stays inside the 100 ms budget.
/// </summary>
public sealed class ElevatorControllerConcurrencyTests
{
    private const int Requests = 2_000;
    private static readonly TimeSpan AssignmentBudget = TimeSpan.FromMilliseconds(100);

    [Fact]
    public async Task RequestElevator_ThousandsAtOnce_AreEachAssignedOnceWithinTheBudget()
    {
        await using var system = await RunningSystem.StartMovingAsync();

        var results = await RunTogether(Requests, _ => RequestRandomHallCall(system.Controller));
        await system.Probe.WaitUntilAsync(() => AssignmentEvents(system.Probe).Count >= Requests, $"{Requests} assignments");

        results.ShouldAllBe(result => result.IsAccepted);
        var accepted = results.Select(result => result.RequestId!.Value).ToHashSet();
        accepted.Count.ShouldBe(Requests, "every request gets its own identifier");
        var assignments = AssignmentEvents(system.Probe);
        assignments.Select(e => e.Request.Id).ShouldBe(accepted, ignoreOrder: true, "each request is assigned exactly once");
        var latencies = assignments.Select(e => e.OccurredAt - e.Request.RequestedAt).Order().ToList();
        var p99 = latencies[(int)Math.Ceiling(latencies.Count * 0.99) - 1];
        p99.ShouldBeLessThan(AssignmentBudget, $"p99 assignment latency was {p99.TotalMilliseconds:F1} ms (max {latencies[^1].TotalMilliseconds:F1} ms)");
        var pending = system.Controller.Elevator.Snapshot().PendingCalls;
        pending.Length.ShouldBeLessThanOrEqualTo(Building.Default.MaxPendingCalls, "merging keeps the queue bounded");
        pending.Sum(call => call.RequestCount).ShouldBe(Requests + 1, "the burst plus the trip that set the car moving");
    }

    [Fact]
    public async Task EmergencyStop_RacingWithRequests_LeavesNoRequestUnaccountedFor()
    {
        await using var system = await RunningSystem.StartMovingAsync();

        var results = await RunTogether(Requests, index =>
        {
            if (index == Requests / 2)
            {
                system.Controller.EmergencyStop();
                return null;
            }

            return RequestRandomHallCall(system.Controller);
        });
        var accepted = results.Where(result => result is { IsAccepted: true }).Select(result => result!.RequestId!.Value).ToHashSet();
        await system.Probe.WaitUntilAsync(() => Outcomes(system.Probe).Count >= accepted.Count, "an outcome for every accepted request");

        var outcomes = Outcomes(system.Probe);
        outcomes.Select(outcome => outcome.Id).ShouldBe(accepted, ignoreOrder: true, "every accepted request is assigned or refused exactly once");
        results.Where(result => result is { IsAccepted: false })
            .ShouldAllBe(result => result!.RejectionReason == RequestRejectionReason.OutOfService);
        var snapshot = system.Controller.Elevator.Snapshot();
        snapshot.State.ShouldBe(ElevatorState.OutOfService);
        snapshot.PendingCalls.ShouldBeEmpty("an out-of-service car holds no calls");
    }

    private static List<CallAssignment> AssignmentEvents(EventProbe probe) =>
    [
        .. probe.Events
            .Select(e => e switch
            {
                CallQueued queued => new CallAssignment(queued.Request, queued.OccurredAt),
                CallMerged merged => new CallAssignment(merged.Request, merged.OccurredAt),
                _ => null,
            })
            .OfType<CallAssignment>()
            .Where(assignment => assignment.Request.Call is HallCall),
    ];

    private static List<(RequestId Id, string Outcome)> Outcomes(EventProbe probe) =>
    [
        .. probe.Events.Select(e => e switch
            {
                CallQueued { Request.Call: HallCall } queued => (queued.Request.Id, "queued"),
                CallMerged { Request.Call: HallCall } merged => (merged.Request.Id, "merged"),
                RequestRejected rejected => (rejected.Request.Id, "rejected"),
                _ => (default(RequestId), string.Empty),
            })
            .Where(outcome => outcome.Item2.Length > 0),
    ];

    /// <summary>A random hall call that is always valid: up from floors 1-9, down from floors 2-10.</summary>
    private static RequestResult RequestRandomHallCall(ElevatorController controller)
    {
        var goingUp = Random.Shared.Next(2) == 0;
        var floor = goingUp ? Random.Shared.Next(1, 10) : Random.Shared.Next(2, 11);
        return controller.RequestElevator(floor, goingUp ? Direction.Up : Direction.Down);
    }

    private static async Task<T[]> RunTogether<T>(int count, Func<int, T> operation)
    {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, count)
            .Select(index => Task.Run(async () =>
            {
                await gate.Task;
                return operation(index);
            }))
            .ToArray();

        gate.SetResult();
        return await Task.WhenAll(tasks);
    }

    private sealed record CallAssignment(PassengerRequest Request, DateTimeOffset OccurredAt);

    /// <summary>A controller on real time whose car is mid-trip for the whole test (trips take minutes).</summary>
    private sealed class RunningSystem : IAsyncDisposable
    {
        private readonly CancellationTokenSource _shutdown = new();
        private Task _loop = Task.CompletedTask;

        private RunningSystem()
        {
            var options = new ElevatorControllerOptions
            {
                TravelTimePerFloor = TimeSpan.FromMinutes(10),
                DoorOpenDuration = TimeSpan.FromMinutes(10),
                IntakeCapacity = Requests * 2,
            };
            Controller = new ElevatorController(
                new Elevator(Building.Default, TimeProvider.System),
                new FifoSchedulingStrategy(),
                new SimulatedElevatorDrive(options.TravelTimePerFloor, TimeProvider.System),
                Probe,
                options,
                TimeProvider.System,
                NullLogger<ElevatorController>.Instance);
        }

        public EventProbe Probe { get; } = new();

        public ElevatorController Controller { get; }

        public static async Task<RunningSystem> StartMovingAsync()
        {
            var system = new RunningSystem();
            system._loop = system.Controller.ProcessRequests(system._shutdown.Token);
            system.Controller.RequestDestination(10).IsAccepted.ShouldBeTrue();
            await system.Probe.WaitForAsync<ElevatorMoved>();
            return system;
        }

        public async ValueTask DisposeAsync()
        {
            await _shutdown.CancelAsync();
            await _loop;
            _shutdown.Dispose();
        }
    }
}
