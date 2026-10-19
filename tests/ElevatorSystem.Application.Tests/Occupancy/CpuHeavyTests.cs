namespace ElevatorSystem.Application.Tests.Occupancy;

/// <summary>
/// Tests that keep every core busy (CsCheck samples on all of them, the generator runs simulate long
/// stretches). They run on their own, after the parallel tests, so they never starve the real-time
/// latency budget checked by <see cref="ElevatorControllerConcurrencyTests"/>.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class CpuHeavyTests
{
    public const string Name = "CPU-heavy";
}
