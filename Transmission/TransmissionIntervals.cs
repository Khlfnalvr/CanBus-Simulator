namespace CanBusSimulator.Transmission;

/// <summary>
/// Per-group transmission periods (milliseconds) that mimic the cadence an
/// ESP master would use when forwarding BMS CAN frames over UART.
/// Defaults follow common BMS practice: fast pack/heartbeat, slow cells.
/// </summary>
public sealed class TransmissionIntervals
{
    /// <summary>Pack status frame 0x100 cadence. Typical: 100 ms.</summary>
    public int PackStatusMs { get; set; } = 100;

    /// <summary>Temperature group frames 0x110-0x112. Typical: 500 ms.</summary>
    public int TemperatureMs { get; set; } = 500;

    /// <summary>Cell-voltage group frames 0x101-0x105. Typical: 200 ms.</summary>
    public int CellVoltageMs { get; set; } = 200;

    /// <summary>Balancing flag frame 0x120. Typical: 1000 ms.</summary>
    public int BalancingMs { get; set; } = 1000;

    /// <summary>Diagnostic / fault frame 0x130. Typical: 500 ms.</summary>
    public int DiagnosticMs { get; set; } = 500;

    /// <summary>Heartbeat / counter frame 0x140. Typical: 250 ms.</summary>
    public int HeartbeatMs { get; set; } = 250;
}
