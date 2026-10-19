using System.Globalization;
using System.Text;
using ElevatorSystem.Application.Occupancy;
using ElevatorSystem.Contracts;

namespace ElevatorSystem.ConsoleDemo;

/// <summary>Prints the passenger counts every time they change, and the full table on request.</summary>
internal sealed class ConsoleOccupancyView(ConsoleOutput output) : IOccupancyObserver
{
    private volatile bool _quiet;

    /// <summary>When set, changes are not printed (used for the burst, where every request is a person).</summary>
    public bool Quiet
    {
        get => _quiet;
        set => _quiet = value;
    }

    public void OccupancyChanged(BuildingOccupancyDto occupancy)
    {
        if (!_quiet)
        {
            output.Event("people", ConsoleColor.Magenta, Summary(occupancy));
        }
    }

    /// <summary>One line: riders against capacity, then only the floors where someone is waiting or present.</summary>
    public static string Summary(BuildingOccupancyDto occupancy)
    {
        var line = new StringBuilder();
        line.Append(CultureInfo.InvariantCulture, $"riding {occupancy.PassengersInCar}/{occupancy.CarCapacity}");
        line.Append(", waiting ").Append(PerFloor(occupancy.Floors.Select(floor => (floor.Floor, floor.Waiting))));
        line.Append(", present ").Append(PerFloor(occupancy.Floors.Select(floor => (floor.Floor, floor.Present))));
        line.Append(CultureInfo.InvariantCulture, $", delivered {occupancy.PassengersDelivered}, average wait {AverageWait(occupancy)}");
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
            $"Riding {occupancy.PassengersInCar} of {occupancy.CarCapacity}; {occupancy.PassengersDelivered} delivered; average wait {AverageWait(occupancy)}."));
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
