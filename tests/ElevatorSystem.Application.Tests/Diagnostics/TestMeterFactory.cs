using System.Diagnostics.Metrics;

namespace ElevatorSystem.Application.Tests.Diagnostics;

/// <summary>Meters scoped to one test, so parallel tests never see each other's measurements.</summary>
internal sealed class TestMeterFactory : IMeterFactory
{
    private readonly List<Meter> _created = [];

    public Meter Create(MeterOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var meter = new Meter(options.Name, options.Version, options.Tags, scope: this);
        _created.Add(meter);
        return meter;
    }

    public void Dispose()
    {
        foreach (var meter in _created)
        {
            meter.Dispose();
        }
    }
}
