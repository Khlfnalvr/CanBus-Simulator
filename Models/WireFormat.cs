namespace CanBusSimulator.Models;

/// <summary>
/// Selectable wire formats produced by the simulator.
/// </summary>
public enum WireFormat
{
    /// <summary>
    /// Human-readable text used by the original BMS Monitor:
    /// <c>$ID:0x100,DLC:8,DATA:0FA0FFEC4B000000\r\n</c>.
    /// </summary>
    Custom = 0,

    /// <summary>
    /// SLCAN / Lawicel standard frame:
    /// <c>t1008<8-bytes-hex>\r</c>. The de-facto format used by ESP32 / USB-CAN bridges.
    /// </summary>
    Slcan = 1,

    /// <summary>
    /// Raw binary frame: [ID hi, ID lo, DLC, data...]. No delimiters.
    /// </summary>
    Binary = 2
}
