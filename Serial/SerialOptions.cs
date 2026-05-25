namespace CanBusSimulator.Serial;

/// <summary>
/// Immutable options used when opening a Windows serial port for UART text output.
/// </summary>
public sealed record SerialOptions(string PortName, int BaudRate);
