using ElevatorSystem.Domain.Scheduling;

namespace ElevatorSystem.Application.Tests;

/// <summary>The scheduling setting (ADR-0005): FIFO unless a deployment chooses LOOK.</summary>
public sealed class SchedulingPolicyTests
{
    [Fact]
    public void ByDefault_TheCarServesFirstInFirstOut() =>
        new ElevatorControllerOptions().CreateStrategy().ShouldBeOfType<FifoSchedulingStrategy>();

    [Fact]
    public void Look_CreatesTheLookStrategy() =>
        new ElevatorControllerOptions { Scheduling = SchedulingPolicy.Look }.CreateStrategy().ShouldBeOfType<LookSchedulingStrategy>();

    [Fact]
    public void AnUnknownPolicy_IsRefused() =>
        Should.Throw<ArgumentOutOfRangeException>(() => new ElevatorControllerOptions { Scheduling = (SchedulingPolicy)7 }.Validate());
}
