namespace CanBusSimulator.Serial;

/// <summary>
/// Immutable options used when opening a Windows serial port.
/// </summary>
public sealed record SerialOptions(string PortName, int BaudRate);
