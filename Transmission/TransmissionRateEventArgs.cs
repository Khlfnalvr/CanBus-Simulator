namespace CanBusSimulator.Transmission;

/// <summary>
/// Event data for the measured transmit rate.
/// </summary>
public sealed class TransmissionRateEventArgs : EventArgs
{
    /// <summary>
    /// Creates a measured-rate event.
    /// </summary>
    public TransmissionRateEventArgs(double framesPerSecond)
    {
        FramesPerSecond = framesPerSecond;
    }

    /// <summary>
    /// Number of CAN protocol lines transmitted per second.
    /// </summary>
    public double FramesPerSecond { get; }
}
