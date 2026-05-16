using CanBusSimulator.Models;

namespace CanBusSimulator.Transmission;

/// <summary>
/// Event data for a successfully transmitted CAN frame.
/// </summary>
public sealed class FrameTransmittedEventArgs : EventArgs
{
    /// <summary>
    /// Creates event data for one transmitted frame.
    /// </summary>
    public FrameTransmittedEventArgs(CanFrame frame, string wireLine, string decodedText, DateTimeOffset timestamp)
    {
        Frame = frame;
        WireLine = wireLine;
        DecodedText = decodedText;
        Timestamp = timestamp;
        Checksum = frame.CalculateChecksum();
    }

    /// <summary>
    /// Frame that was sent.
    /// </summary>
    public CanFrame Frame { get; }

    /// <summary>
    /// Exact protocol line written to the COM port without trailing CRLF.
    /// </summary>
    public string WireLine { get; }

    /// <summary>
    /// Human-readable decoded BMS values.
    /// </summary>
    public string DecodedText { get; }

    /// <summary>
    /// Transmission timestamp.
    /// </summary>
    public DateTimeOffset Timestamp { get; }

    /// <summary>
    /// XOR checksum calculated over raw frame bytes.
    /// </summary>
    public byte Checksum { get; }
}
