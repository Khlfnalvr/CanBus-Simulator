namespace CanBusSimulator.Transmission;

/// <summary>
/// Cadence for emitting JSON snapshots to BMS Monitor.
/// </summary>
public sealed class TransmissionIntervals
{
    /// <summary>Interval between JSON snapshots, in milliseconds. Typical: 200 ms.</summary>
    public int SendIntervalMs { get; set; } = 200;
}
