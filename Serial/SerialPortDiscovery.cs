using Microsoft.Win32;

namespace CanBusSimulator.Serial;

/// <summary>
/// Describes a Windows COM port discovered from the registry.
/// </summary>
public sealed record SerialPortInfo(string PortName, string DeviceName)
{
    /// <summary>
    /// User-facing display text for combo boxes and logs.
    /// </summary>
    public string DisplayName => string.IsNullOrWhiteSpace(DeviceName)
        ? PortName
        : $"{PortName} ({DeviceName})";

    /// <summary>
    /// True when Windows reports the port as a Bluetooth modem endpoint.
    /// </summary>
    public bool IsBluetooth => DeviceName.Contains("BthModem", StringComparison.OrdinalIgnoreCase);

    /// <inheritdoc />
    public override string ToString() => DisplayName;
}

/// <summary>
/// Enumerates Windows COM ports from the system registry.
/// </summary>
public static class SerialPortDiscovery
{
    /// <summary>
    /// Returns available COM port names such as COM5 and COM6.
    /// </summary>
    public static IReadOnlyList<string> GetAvailablePorts()
    {
        return GetAvailablePortInfos().Select(port => port.PortName).ToArray();
    }

    /// <summary>
    /// Returns available COM port names with the backing Windows device path.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> GetAvailablePortInfos()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
        if (key is null)
        {
            return Array.Empty<SerialPortInfo>();
        }

        return key.GetValueNames()
            .Select(name => new SerialPortInfo(key.GetValue(name)?.ToString() ?? string.Empty, name))
            .Where(port => !string.IsNullOrWhiteSpace(port.PortName))
            .GroupBy(port => port.PortName, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(port => GetNumericPortIndex(port.PortName))
            .ThenBy(port => port.PortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static int GetNumericPortIndex(string portName)
    {
        if (portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && int.TryParse(portName[3..], out var number))
        {
            return number;
        }

        return int.MaxValue;
    }
}
