namespace ElevatorSystem.Domain;

/// <summary>A direction of travel, and the direction a passenger wants to go when calling the car.</summary>
public enum Direction
{
    Up,
    Down,
}

public static class DirectionExtensions
{
    public static Direction Opposite(this Direction direction) =>
        direction == Direction.Up ? Direction.Down : Direction.Up;
}
