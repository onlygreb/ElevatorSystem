using ElevatorSystem.Contracts;
using Microsoft.JSInterop;

namespace ElevatorSystem.Web.Services;

/// <summary>The step a passenger is at, which decides what the passenger view offers.</summary>
public enum JourneyStage
{
    /// <summary>Not in the building yet: they can arrive.</summary>
    Outside,

    /// <summary>On a floor, not waiting: they can call the car (or walk in, if its door is open here).</summary>
    OnFloor,

    /// <summary>On a floor, having called: the car is on its way.</summary>
    Waiting,

    /// <summary>The car's door is open on their floor: they can walk in.</summary>
    DoorOpenHere,

    /// <summary>Inside the car: they choose a floor, and walk out whenever the door opens.</summary>
    InCar,
}

/// <summary>
/// This tab's passenger (ADR-0026, ADR-0035): a random id kept in <c>sessionStorage</c>, so a reload keeps the journey
/// while every other tab is a passenger of its own, and where they are, followed from their own activities on the hub. The server decides every step; this only remembers the last
/// answer and why a step was refused.
/// </summary>
public sealed class PassengerJourney : IDisposable
{
    public const string StorageKey = "elevator.passenger";

    private readonly IPassengerApiClient _api;
    private readonly ElevatorStateStore _store;
    private readonly IJSRuntime _js;

    public PassengerJourney(IPassengerApiClient api, ElevatorStateStore store, IJSRuntime js)
    {
        _api = api;
        _store = store;
        _js = js;
        _store.ActivityReceived += OnActivity;
    }

    public event EventHandler? Changed;

    /// <summary>The passenger, once they arrived; <see langword="null"/> while outside.</summary>
    public PassengerDto? Me { get; private set; }

    /// <summary>Why the last step was refused, until the next step; <see langword="null"/> otherwise.</summary>
    public PassengerRefusalReason? Refusal { get; private set; }

    /// <summary>A message for a step that never reached the engine (network, rate limit); <see langword="null"/> otherwise.</summary>
    public string? Problem { get; private set; }

    /// <summary>A step is on its way, so buttons do not send it twice.</summary>
    public bool Busy { get; private set; }

    /// <summary>Where the passenger is in their journey, given the car's current status.</summary>
    public JourneyStage Stage(ElevatorStatusDto? status) => Me switch
    {
        null => JourneyStage.Outside,
        { InCar: true } => JourneyStage.InCar,
        { } me when status is { Status: ElevatorStatus.DoorOpen } && status.CurrentFloor == me.Floor => JourneyStage.DoorOpenHere,
        { Waiting: not null } => JourneyStage.Waiting,
        _ => JourneyStage.OnFloor,
    };

    /// <summary>The floor the passenger is on, or the car's floor while they ride.</summary>
    public int? Floor(ElevatorStatusDto? status) => Me switch
    {
        null => null,
        { InCar: true } when status is not null => status.CurrentFloor,
        { } me => me.Floor,
    };

    /// <summary>Picks up the journey this tab left off, if the building still knows the passenger.</summary>
    public async Task RestoreAsync()
    {
        string? stored = null;
        try
        {
            stored = await _js.InvokeAsync<string?>("sessionStorage.getItem", StorageKey);
        }
        catch (JSException)
        {
            // Storage blocked: every visit starts outside.
        }

        if (Guid.TryParse(stored, out var id) && await _api.GetAsync(id) is { } me)
        {
            Me = me;
            OnChanged();
        }
    }

    public async Task ArriveAsync()
    {
        await RunAsync(async () =>
        {
            var (accepted, outcome) = await _api.JoinAsync();
            if (accepted is not null)
            {
                Me = new PassengerDto(accepted.PassengerId, _store.Configuration?.LowestFloor ?? 1, false, null, null, false);
                await RememberAsync(accepted.PassengerId);
            }

            return outcome;
        });
    }

    public Task CallAsync(CallDirection direction) => StepAsync(id => _api.CallElevatorAsync(id, direction));

    public Task BoardAsync() => StepAsync(id => _api.BoardAsync(id));

    public Task ChooseFloorAsync(int floor) => StepAsync(id => _api.ChooseFloorAsync(id, floor));

    public Task LeaveAsync() => StepAsync(id => _api.LeaveAsync(id));

    public void Dispose() => _store.ActivityReceived -= OnActivity;

    private Task StepAsync(Func<Guid, Task<RequestOutcome>> step) =>
        Me is { } me ? RunAsync(() => step(me.PassengerId)) : Task.CompletedTask;

    private async Task RunAsync(Func<Task<RequestOutcome>> step)
    {
        if (Busy)
        {
            return;
        }

        Busy = true;
        Refusal = null;
        Problem = null;
        OnChanged();
        try
        {
            var outcome = await step();
            Problem = outcome.IsAccepted ? null : outcome.Message;
        }
        finally
        {
            Busy = false;
            OnChanged();
        }
    }

    /// <summary>Follows this passenger's own activities, the same way the server's cache does.</summary>
    private void OnActivity(object? sender, ElevatorActivityDto activity)
    {
        if (Me is not { } me || activity.PassengerId != me.PassengerId)
        {
            return;
        }

        switch (activity.Kind)
        {
            case ElevatorActivityKind.PassengerJoined:
                Me = me with { Floor = activity.Floor };
                break;
            case ElevatorActivityKind.PassengerCalled:
                Me = me with { Waiting = activity.Direction };
                break;
            case ElevatorActivityKind.PassengerBoarded:
                Me = me with { InCar = true, Waiting = null, Floor = activity.Floor };
                break;
            case ElevatorActivityKind.PassengerChoseFloor:
                Me = me with { Destination = activity.Floor };
                break;
            case ElevatorActivityKind.PassengerLeft:
                Me = me with { InCar = false, Floor = activity.Floor, Destination = null };
                break;
            case ElevatorActivityKind.PassengerRemoved:
                Me = null;
                break;
            case ElevatorActivityKind.PassengerRefused when activity.PassengerRefusal == PassengerRefusalReason.UnknownPassenger:
                // The building forgot them (a restart, or they were away too long): they arrive again.
                Me = null;
                Refusal = PassengerRefusalReason.UnknownPassenger;
                break;
            case ElevatorActivityKind.PassengerRefused:
                Refusal = activity.PassengerRefusal;
                break;
            default:
                return;
        }

        OnChanged();
    }

    private async Task RememberAsync(Guid passengerId)
    {
        try
        {
            await _js.InvokeVoidAsync("sessionStorage.setItem", StorageKey, passengerId.ToString("D"));
        }
        catch (JSException)
        {
            // Storage blocked: the journey simply does not survive a reload.
        }
    }

    private void OnChanged() => Changed?.Invoke(this, EventArgs.Empty);
}
