using System.IO.Ports;
using System.Text;

namespace CanBusSimulator.Serial;

/// <summary>
/// Serial transport built on System.IO.Ports.SerialPort.
/// Mirrors the behavior expected by the ESP master forwarding CAN frames over UART.
/// </summary>
public sealed class SerialPortTransport : ISerialTransport
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SerialPort? _port;

    public event EventHandler<string>? StatusChanged;

    public bool IsOpen => _port is { IsOpen: true };

    public SerialOptions? CurrentOptions { get; private set; }

    public async Task OpenAsync(SerialOptions options, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            CloseNoLock();
            OpenNoLock(options);
            CurrentOptions = options;
            StatusChanged?.Invoke(this, $"Connected to {options.PortName} at {options.BaudRate} baud.");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<bool> TryReconnectAsync(CancellationToken cancellationToken)
    {
        var options = CurrentOptions;
        if (options is null) return false;

        try
        {
            await OpenAsync(options, cancellationToken).ConfigureAwait(false);
            StatusChanged?.Invoke(this, $"Reconnected to {options.PortName}.");
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task WriteBytesAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_port is null || !_port.IsOpen)
            {
                throw new InvalidOperationException("Serial port is not open.");
            }

            await _port.BaseStream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Close()
    {
        _gate.Wait();
        try
        {
            CloseNoLock();
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        Close();
        _gate.Dispose();
    }

    private void OpenNoLock(SerialOptions options)
    {
        var port = new SerialPort(options.PortName, options.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = Handshake.None,
            DtrEnable = true,
            RtsEnable = true,
            ReadTimeout = 500,
            WriteTimeout = 1000,
            WriteBufferSize = 8192,
            ReadBufferSize = 8192,
            NewLine = "\r\n",
            Encoding = Encoding.ASCII
        };

        port.Open();
        port.DiscardInBuffer();
        port.DiscardOutBuffer();
        _port = port;
    }

    private void CloseNoLock()
    {
        if (_port is null) return;

        try
        {
            if (_port.IsOpen)
            {
                _port.Close();
                StatusChanged?.Invoke(this, "Serial port closed.");
            }
        }
        catch
        {
            // best-effort close
        }
        finally
        {
            _port.Dispose();
            _port = null;
        }
    }
}
