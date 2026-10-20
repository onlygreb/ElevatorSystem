namespace ElevatorSystem.E2E.Tests;

/// <summary>One elevator serves every test, so the test classes share one collection and take turns.</summary>
[CollectionDefinition(Name)]
public sealed class OneElevator
{
    public const string Name = "One elevator";
}
