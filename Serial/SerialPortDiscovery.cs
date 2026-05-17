using System.IO.Ports;
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
/// Enumerates Windows COM ports, combining SerialPort.GetPortNames with registry device names.
/// </summary>
public static class SerialPortDiscovery
{
    /// <summary>
    /// Returns available COM port names such as COM5 and COM6.
    /// </summary>
    public static IReadOnlyList<string> GetAvailablePorts()
    {
        return SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(GetNumericPortIndex)
            .ThenBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// Returns available COM port names with the backing Windows device path.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> GetAvailablePortInfos()
    {
        var deviceMap = BuildDeviceNameMap();
        return SerialPort.GetPortNames()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(port => new SerialPortInfo(port, deviceMap.TryGetValue(port, out var device) ? device : string.Empty))
            .OrderBy(port => GetNumericPortIndex(port.PortName))
            .ThenBy(port => port.PortName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static Dictionary<string, string> BuildDeviceNameMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null)
            {
                return map;
            }

            foreach (var name in key.GetValueNames())
            {
                var portName = key.GetValue(name)?.ToString();
                if (!string.IsNullOrWhiteSpace(portName))
                {
                    map[portName] = name;
                }
            }
        }
        catch
        {
            // registry access can fail in sandboxed environments; ports still listed by SerialPort
        }

        return map;
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
