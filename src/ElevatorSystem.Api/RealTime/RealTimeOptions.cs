namespace ElevatorSystem.Api.RealTime;

/// <summary>Settings of the real-time fan-out, bound from the <c>RealTime</c> configuration section.</summary>
internal sealed class RealTimeOptions
{
    public const string Section = "RealTime";

    /// <summary>How many updates may wait for the broadcaster before the oldest is dropped.</summary>
    [System.ComponentModel.DataAnnotations.Range(1, 1_000_000)]
    public int EventBufferCapacity { get; set; } = 1024;
}
