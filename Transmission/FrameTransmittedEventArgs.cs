namespace CanBusSimulator.Transmission;

/// <summary>
/// Event data for a successfully transmitted JSON line.
/// </summary>
public sealed class FrameTransmittedEventArgs : EventArgs
{
    public FrameTransmittedEventArgs(string wireLine, string decodedText, DateTimeOffset timestamp)
    {
        WireLine = wireLine;
        DecodedText = decodedText;
        Timestamp = timestamp;
    }

    /// <summary>Exact JSON line written to the COM port without trailing newline.</summary>
    public string WireLine { get; }

    /// <summary>Human-readable summary of the BMS values just sent.</summary>
    public string DecodedText { get; }

    /// <summary>Transmission timestamp.</summary>
    public DateTimeOffset Timestamp { get; }
}
