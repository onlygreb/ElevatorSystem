using System.Globalization;
using System.Text;
using ElevatorSystem.Application.Passengers;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Prints each passenger's steps and the counts every time they change, and the full table on request.</summary>
internal sealed class ConsoleOccupancyView(ConsoleOutput output) : IPassengerObserver
{
    private volatile bool _quiet;

    /// <summary>When set, nothing is printed (used for the burst, and while a scenario runs in the background).</summary>
    public bool Quiet
    {
        get => _quiet;
        set => _quiet = value;
    }

    public void PassengerChanged(PassengerEvent passengerEvent)
    {
        if (_quiet)
        {
            return;
        }

        var who = Name(passengerEvent.PassengerId);
        var line = passengerEvent switch
        {
            PassengerJoined joined => $"{who} arrives on floor {joined.Floor.Number}",
            PassengerCalled called => $"{who} presses {called.Direction.ToString().ToLowerInvariant()} on floor {called.Floor.Number}",
            PassengerBoarded boarded => $"{who} walks into the car on floor {boarded.Floor.Number}",
            PassengerChoseFloor chose => $"{who} chooses floor {chose.Destination.Number}",
            PassengerLeft left => $"{who} walks out on floor {left.Floor.Number}",
            PassengerRemoved removed => $"{who} leaves the building from floor {removed.Floor.Number}",
            PassengerRefused refused => $"{who} cannot {refused.Action}: {refused.Reason}",
            _ => null,
        };

        if (line is not null)
        {
            output.Event("passenger", ConsoleColor.Cyan, line);
        }
    }

    public void OccupancyChanged(BuildingOccupancyDto occupancy)
    {
        if (!_quiet)
        {
            output.Event("people", ConsoleColor.Magenta, Summary(occupancy));
        }
    }

    /// <summary>A short, stable name for an anonymous passenger.</summary>
    public static string Name(Guid passengerId) => "passenger " + passengerId.ToString("N")[..4];

    /// <summary>One line: riders against capacity, then only the floors where someone is waiting or present.</summary>
    public static string Summary(BuildingOccupancyDto occupancy)
    {
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"riding {occupancy.PassengersInCar}/{occupancy.CarCapacity}");
        line.Append(", waiting ").Append(PerFloor(occupancy.Floors.Select(floor => (floor.Floor, floor.Waiting))));
        line.Append(", present ").Append(PerFloor(occupancy.Floors.Select(floor => (floor.Floor, floor.Present))));
        line.Append(CultureInfo.InvariantCulture, $", rides {occupancy.PassengersDelivered}, average wait {AverageWait(occupancy)}");
        return line.ToString();
    }

    /// <summary>The full table, top floor first, as the shaft is usually drawn.</summary>
    public void PrintTable(BuildingOccupancyDto occupancy)
    {
        output.Result("Floor  Waiting  Present");
        foreach (var floor in occupancy.Floors.Reverse())
        {
            output.Result(string.Create(CultureInfo.InvariantCulture, $"{floor.Floor,5}  {floor.Waiting,7}  {floor.Present,7}"));
        }

        output.Result(string.Create(
            CultureInfo.InvariantCulture,
            $"Riding {occupancy.PassengersInCar} of {occupancy.CarCapacity}; {occupancy.PassengersDelivered} rides completed; average wait {AverageWait(occupancy)}."));
    }

    private static string PerFloor(IEnumerable<(int Floor, int Count)> floors)
    {
        var occupied = floors.Where(floor => floor.Count > 0).Select(floor => $"{floor.Floor}:{floor.Count}").ToList();
        return occupied.Count == 0 ? "none" : string.Join(' ', occupied);
    }

    private static string AverageWait(BuildingOccupancyDto occupancy) => occupancy.AverageWaitTime is { } wait
        ? string.Create(CultureInfo.InvariantCulture, $"{wait.TotalSeconds:0.0} s")
        : "-";
}
