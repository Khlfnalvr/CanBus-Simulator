namespace CanBusSimulator.Serial;

/// <summary>
/// Abstraction for opening and writing to a serial transport.
/// </summary>
public interface ISerialTransport : IDisposable
{
    /// <summary>
    /// Raised when the connection opens, closes, or reconnects.
    /// </summary>
    event EventHandler<string>? StatusChanged;

    /// <summary>
    /// True when the underlying COM handle is currently open.
    /// </summary>
    bool IsOpen { get; }

    /// <summary>
    /// Last successfully requested serial options, used by reconnect logic.
    /// </summary>
    SerialOptions? CurrentOptions { get; }

    /// <summary>
    /// Opens the configured COM port.
    /// </summary>
    Task OpenAsync(SerialOptions options, CancellationToken cancellationToken);

    /// <summary>
    /// Attempts to reopen the last configured COM port.
    /// </summary>
    Task<bool> TryReconnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes an ASCII protocol line to the COM port.
    /// </summary>
    Task WriteLineAsync(string line, CancellationToken cancellationToken);

    /// <summary>
    /// Writes raw bytes to the COM port (used by binary frame formats).
    /// </summary>
    Task WriteBytesAsync(byte[] bytes, CancellationToken cancellationToken);

    /// <summary>
    /// Closes the COM port if it is open.
    /// </summary>
    void Close();
}
