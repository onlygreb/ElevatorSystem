using System.Globalization;

namespace ElevatorSystem.Domain;

/// <summary>
/// A floor number. Whether a floor exists is decided by the <see cref="Building"/> it belongs to.
/// </summary>
public readonly record struct Floor(int Number) : IComparable<Floor>
{
    /// <summary>The floor directly above this one.</summary>
    public Floor Above => new(Number + 1);

    /// <summary>The floor directly below this one.</summary>
    public Floor Below => new(Number - 1);

    public int CompareTo(Floor other) => Number.CompareTo(other.Number);

    public static bool operator <(Floor left, Floor right) => left.Number < right.Number;

    public static bool operator >(Floor left, Floor right) => left.Number > right.Number;

    public static bool operator <=(Floor left, Floor right) => left.Number <= right.Number;

    public static bool operator >=(Floor left, Floor right) => left.Number >= right.Number;

    public override string ToString() => Number.ToString(CultureInfo.InvariantCulture);
}
