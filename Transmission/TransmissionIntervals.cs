namespace CanBusSimulator.Transmission;

/// <summary>
/// Message intervals in milliseconds for the three BMS CAN frames.
/// </summary>
public sealed class TransmissionIntervals
{
    /// <summary>
    /// Interval for CAN ID 0x100.
    /// </summary>
    public int PackStatusMs { get; set; } = 1000;

    /// <summary>
    /// Interval for CAN ID 0x101.
    /// </summary>
    public int TemperatureMs { get; set; } = 1000;

    /// <summary>
    /// Interval for CAN ID 0x102.
    /// </summary>
    public int CellVoltageMs { get; set; } = 1000;
}
