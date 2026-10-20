using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using ElevatorSystem.Domain;
using ElevatorSystem.Domain.Events;
using Microsoft.Extensions.Logging;
using Log = ElevatorSystem.Application.Hosting.ElevatorEngineLog;

namespace ElevatorSystem.Application.Hosting;

/// <summary>
/// Applies the commands the leader consumes: passenger calls go to the controller with the command's id
/// as their request id, service commands go to its safety methods. Each command is applied at most once.
/// </summary>
/// <remarks>
/// <para>
/// A command is marked as processed <em>before</em> it is applied, so a redelivered copy is always dropped.
/// The price is a narrow window: a crash after the mark and before the next snapshot save loses that call,
/// and the passenger presses again. Marking after applying would instead apply it twice (ADR-0021).
/// When the deduplication store itself is unreachable the command is applied anyway: a repeated call
/// merges into the pending one, while a dropped call strands a passenger (ADR-0007).
/// </para>
/// <para>
/// A call the controller refuses (an invalid floor, an out-of-service car, a full intake) is reported as a
/// <see cref="RequestRejected"/> activity, because the API already answered <c>202</c> and the caller can
/// only learn about it from the updates.
/// </para>
/// </remarks>
internal sealed class EngineCommandHandler(
    ElevatorController controller,
    IProcessedCommandStore processedCommands,
    EngineEventSink sink,
    ElevatorEngineOptions options,
    TimeProvider timeProvider,
    ILogger logger,
    CancellationToken drivingToken)
{
    public async Task HandleAsync(ElevatorCommandV1 command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var type = CommandType(command);
        if (!await TryMarkProcessedAsync(command, type, cancellationToken))
        {
            Log.DuplicateCommand(logger, command.CommandId, type);
            return;
        }

        // From here on the command counts as applied, so it is carried through even if consumption is
        // stopping: only the end of this engine's term (drivingToken) cuts the wait short.
        switch (command)
        {
            case HallCallCommandV1 { Direction: var direction } hall when Enum.IsDefined(direction):
                await SubmitAsync(command, new HallCall(new Floor(hall.Floor), ElevatorContractMapper.ToDomain(direction)));
                break;
            case CarCallCommandV1 car:
                await SubmitAsync(command, new CarCall(new Floor(car.Floor)));
                break;
            case ServiceCommandV1 service when Enum.IsDefined(service.Action):
                Apply(service);
                break;
            default:
                Log.UnknownCommand(logger, command.CommandId, type);
                break;
        }
    }

    private async Task<bool> TryMarkProcessedAsync(ElevatorCommandV1 command, string type, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = new CancellationTokenSource(options.StoreTimeout, timeProvider);
            using var mark = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
            return await processedCommands.TryMarkProcessedAsync(command.CommandId, mark.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Not applied: the consumer returns the command to the queue for the next leader.
            throw;
        }
        catch (Exception exception)
        {
            Log.DeduplicationUnavailable(logger, exception, command.CommandId, type);
            return true;
        }
    }

    private async Task SubmitAsync(ElevatorCommandV1 command, ElevatorCall call)
    {
        var request = new PassengerRequest(new RequestId(command.CommandId), call, command.IssuedAt);
        var takenIn = sink.WhenTakenIn(request.Id);
        var result = controller.Submit(request);
        if (!result.IsAccepted)
        {
            sink.Forget(request.Id);
            sink.Publish(new RequestRejected(timeProvider.GetUtcNow(), call.Floor, request, result.RejectionReason!.Value));
            return;
        }

        // The loop applies it within moments. Waiting means a graceful stop, which ends consumption before
        // driving, never acknowledges a call that is still in the controller's intake.
        try
        {
            using var timeout = new CancellationTokenSource(options.StoreTimeout, timeProvider);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(drivingToken, timeout.Token);
            await takenIn.WaitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            sink.Forget(request.Id);
        }
    }

    private void Apply(ServiceCommandV1 command)
    {
        var action = command.Action.ToString();
        var result = command.Action switch
        {
            ServiceAction.StartMaintenance => controller.StartMaintenance(),
            ServiceAction.EmergencyStop => controller.EmergencyStop(),
            _ => controller.ReturnToService(),
        };

        if (result.Error is { } error)
        {
            Log.ServiceCommandRefused(logger, command.CommandId, action, error);
        }
        else
        {
            Log.ServiceCommandApplied(logger, command.CommandId, action);
        }
    }

    private static string CommandType(ElevatorCommandV1 command) => command switch
    {
        HallCallCommandV1 => "hall-call.v1",
        CarCallCommandV1 => "car-call.v1",
        ServiceCommandV1 => "service.v1",
        _ => command.GetType().Name,
    };
}
