using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CanBusSimulator.Serial;

/// <summary>
/// Serial transport implemented with Win32 APIs to avoid external NuGet dependencies.
/// </summary>
public sealed class Win32SerialTransport : ISerialTransport
{
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x80;
    private const uint PurgeTxClear = 0x0004;
    private const uint PurgeRxClear = 0x0008;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private SafeFileHandle? _handle;

    /// <inheritdoc />
    public event EventHandler<string>? StatusChanged;

    /// <inheritdoc />
    public bool IsOpen => _handle is { IsInvalid: false, IsClosed: false };

    /// <inheritdoc />
    public SerialOptions? CurrentOptions { get; private set; }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public async Task<bool> TryReconnectAsync(CancellationToken cancellationToken)
    {
        var options = CurrentOptions;
        if (options is null)
        {
            return false;
        }

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

    /// <inheritdoc />
    public async Task WriteLineAsync(string line, CancellationToken cancellationToken)
    {
        var bytes = Encoding.ASCII.GetBytes(line);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsOpen || _handle is null)
            {
                throw new InvalidOperationException("Serial port is not open.");
            }

            if (!WriteFile(_handle, bytes, bytes.Length, out var written, IntPtr.Zero))
            {
                var error = new Win32Exception(Marshal.GetLastWin32Error());
                CloseNoLock();
                throw new IOException($"Failed to write to serial port: {error.Message}", error);
            }

            if (written != bytes.Length)
            {
                CloseNoLock();
                throw new IOException($"Serial write incomplete. Expected {bytes.Length} bytes, wrote {written} bytes.");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
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

    /// <inheritdoc />
    public void Dispose()
    {
        Close();
        _gate.Dispose();
    }

    private void OpenNoLock(SerialOptions options)
    {
        var path = ToDevicePath(options.PortName);
        var handle = CreateFile(path, GenericRead | GenericWrite, 0, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new IOException($"Unable to open {options.PortName}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        ConfigurePort(handle, options.BaudRate);
        _handle = handle;
    }

    private void ConfigurePort(SafeFileHandle handle, int baudRate)
    {
        if (!SetupComm(handle, 4096, 4096))
        {
            throw new IOException($"SetupComm failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        var dcb = new Dcb { DCBlength = (uint)Marshal.SizeOf<Dcb>() };
        if (!GetCommState(handle, ref dcb))
        {
            throw new IOException($"GetCommState failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        dcb.BaudRate = (uint)baudRate;
        dcb.Flags = 0x00000001;
        dcb.ByteSize = 8;
        dcb.Parity = 0;
        dcb.StopBits = 0;

        if (!SetCommState(handle, ref dcb))
        {
            throw new IOException($"SetCommState failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        var timeouts = new CommTimeouts
        {
            ReadIntervalTimeout = 50,
            ReadTotalTimeoutMultiplier = 10,
            ReadTotalTimeoutConstant = 50,
            WriteTotalTimeoutMultiplier = 10,
            WriteTotalTimeoutConstant = 500
        };

        if (!SetCommTimeouts(handle, ref timeouts))
        {
            throw new IOException($"SetCommTimeouts failed: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }

        _ = PurgeComm(handle, PurgeTxClear | PurgeRxClear);
    }

    private void CloseNoLock()
    {
        if (_handle is { IsClosed: false })
        {
            _handle.Dispose();
            StatusChanged?.Invoke(this, "Serial port closed.");
        }

        _handle = null;
    }

    private static string ToDevicePath(string portName)
    {
        return portName.StartsWith(@"\\.\", StringComparison.Ordinal)
            ? portName
            : $@"\\.\{portName}";
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetupComm(SafeFileHandle hFile, uint dwInQueue, uint dwOutQueue);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetCommState(SafeFileHandle hFile, ref Dcb lpDcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommState(SafeFileHandle hFile, ref Dcb lpDcb);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetCommTimeouts(SafeFileHandle hFile, ref CommTimeouts lpCommTimeouts);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PurgeComm(SafeFileHandle hFile, uint dwFlags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(
        SafeFileHandle hFile,
        byte[] lpBuffer,
        int nNumberOfBytesToWrite,
        out int lpNumberOfBytesWritten,
        IntPtr lpOverlapped);

    [StructLayout(LayoutKind.Sequential)]
    private struct Dcb
    {
        public uint DCBlength;
        public uint BaudRate;
        public uint Flags;
        public ushort wReserved;
        public ushort XonLim;
        public ushort XoffLim;
        public byte ByteSize;
        public byte Parity;
        public byte StopBits;
        public byte XonChar;
        public byte XoffChar;
        public byte ErrorChar;
        public byte EofChar;
        public byte EvtChar;
        public ushort wReserved1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CommTimeouts
    {
        public uint ReadIntervalTimeout;
        public uint ReadTotalTimeoutMultiplier;
        public uint ReadTotalTimeoutConstant;
        public uint WriteTotalTimeoutMultiplier;
        public uint WriteTotalTimeoutConstant;
    }
}
