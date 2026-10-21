using ElevatorSystem.Api.Passengers;
using ElevatorSystem.Application.Ports;
using ElevatorSystem.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace ElevatorSystem.Api.Tests.Passengers;

/// <summary>A passenger whose tab closed leaves the building, unless they are back within the grace period (ADR-0036).</summary>
public sealed class PassengerDeparturesTests : IDisposable
{
    private readonly FakeTimeProvider _time = new(ApiFactory.StartTime);
    private readonly RecordingPublisher _publisher = new();
    private readonly PassengerDepartures _departures;

    public PassengerDeparturesTests() =>
        _departures = new PassengerDepartures(_publisher, _time, new ConfigurationBuilder().Build(), NullLogger<PassengerDepartures>.Instance);

    [Fact]
    public void AfterTheGrace_ThePassengerLeavesTheBuilding()
    {
        var passenger = Guid.NewGuid();

        _departures.Schedule(passenger).ShouldBeTrue();
        _time.Advance(PassengerDepartures.DefaultGrace - TimeSpan.FromMilliseconds(1));
        _publisher.Commands.ShouldBeEmpty("a reloading tab has until the end of the grace to come back");
        _time.Advance(TimeSpan.FromMilliseconds(1));

        var departure = _publisher.Commands.ShouldHaveSingleItem().ShouldBeOfType<PassengerCommandV1>();
        departure.PassengerId.ShouldBe(passenger);
        departure.Action.ShouldBe(PassengerAction.LeaveBuilding);
        _departures.Pending.ShouldBe(0);
    }

    [Fact]
    public void HeardFromWithinTheGrace_ThePassengerStays()
    {
        var passenger = Guid.NewGuid();
        _departures.Schedule(passenger);

        _departures.Cancel(passenger);
        _time.Advance(PassengerDepartures.DefaultGrace * 2);

        _publisher.Commands.ShouldBeEmpty();
    }

    [Fact]
    public void AnnouncedTwice_LeavesOnce_AtTheFirstDeadline()
    {
        var passenger = Guid.NewGuid();
        _departures.Schedule(passenger);
        _time.Advance(TimeSpan.FromSeconds(4));

        _departures.Schedule(passenger);
        _time.Advance(PassengerDepartures.DefaultGrace - TimeSpan.FromSeconds(4));

        _publisher.Commands.ShouldHaveSingleItem();
        _time.Advance(PassengerDepartures.DefaultGrace);
        _publisher.Commands.ShouldHaveSingleItem();
    }

    [Fact]
    public void TooManyWaiting_AreRefused()
    {
        for (var index = 0; index < PassengerDepartures.MaxPending; index++)
        {
            _departures.Schedule(Guid.NewGuid()).ShouldBeTrue();
        }

        _departures.Schedule(Guid.NewGuid()).ShouldBeFalse();
    }

    public void Dispose() => _departures.Dispose();

    private sealed class RecordingPublisher : IElevatorCommandPublisher
    {
        private readonly List<ElevatorCommandV1> _commands = [];

        public IReadOnlyList<ElevatorCommandV1> Commands
        {
            get
            {
                lock (_commands)
                {
                    return [.. _commands];
                }
            }
        }

        public Task PublishAsync(ElevatorCommandV1 command, CancellationToken cancellationToken)
        {
            lock (_commands)
            {
                _commands.Add(command);
            }

            return Task.CompletedTask;
        }
    }
}
