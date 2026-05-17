using System.Text;

namespace CanBusSimulator.Models;

/// <summary>
/// Represents a CAN 2.0B standard frame with an 11-bit identifier.
/// </summary>
public sealed class CanFrame
{
    /// <summary>
    /// Creates a frame and validates CAN identifier, DLC, and payload length.
    /// </summary>
    public CanFrame(ushort identifier, byte dlc, ReadOnlySpan<byte> payload)
    {
        if (identifier > 0x7FF)
        {
            throw new ArgumentOutOfRangeException(nameof(identifier), "CAN standard identifier must be 0x000-0x7FF.");
        }

        if (dlc > 8)
        {
            throw new ArgumentOutOfRangeException(nameof(dlc), "DLC must be 0-8.");
        }

        if (payload.Length != dlc)
        {
            throw new ArgumentException("Payload length must match DLC.", nameof(payload));
        }

        Identifier = identifier;
        Dlc = dlc;
        Payload = payload.ToArray();
    }

    /// <summary>
    /// 11-bit CAN identifier.
    /// </summary>
    public ushort Identifier { get; }

    /// <summary>
    /// Data Length Code, from 0 to 8.
    /// </summary>
    public byte Dlc { get; }

    /// <summary>
    /// Payload bytes. The length always equals DLC.
    /// </summary>
    public byte[] Payload { get; }

    /// <summary>
    /// Builds a CAN frame from an identifier and payload.
    /// </summary>
    public static CanFrame FromPayload(ushort identifier, params byte[] payload)
    {
        return new CanFrame(identifier, (byte)payload.Length, payload);
    }

    /// <summary>
    /// Returns the raw binary representation: CAN_ID big-endian, DLC, and payload bytes.
    /// </summary>
    public byte[] ToRawBinary()
    {
        var raw = new byte[3 + Payload.Length];
        raw[0] = (byte)(Identifier >> 8);
        raw[1] = (byte)(Identifier & 0xFF);
        raw[2] = Dlc;
        Payload.CopyTo(raw.AsSpan(3));
        return raw;
    }

    /// <summary>
    /// Calculates an 8-bit XOR checksum over the raw binary frame bytes.
    /// </summary>
    public byte CalculateChecksum()
    {
        byte checksum = 0;
        foreach (var value in ToRawBinary())
        {
            checksum ^= value;
        }

        return checksum;
    }

    /// <summary>
    /// Renders the frame using the requested wire format. Text formats include the trailing terminator.
    /// </summary>
    public byte[] Render(WireFormat format, bool includeChecksum)
    {
        return format switch
        {
            WireFormat.Custom => Encoding.ASCII.GetBytes(ToCustomString(includeChecksum)),
            WireFormat.Slcan => Encoding.ASCII.GetBytes(ToSlcanString()),
            WireFormat.Binary => ToBinaryFrame(includeChecksum),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown wire format.")
        };
    }

    /// <summary>
    /// Returns the human-readable representation for a UI log line.
    /// </summary>
    public string ToDisplayString(WireFormat format, bool includeChecksum)
    {
        return format switch
        {
            WireFormat.Custom => ToCustomString(includeChecksum).TrimEnd('\r', '\n'),
            WireFormat.Slcan => ToSlcanString().TrimEnd('\r'),
            WireFormat.Binary => "BIN " + Convert.ToHexString(ToBinaryFrame(includeChecksum)),
            _ => string.Empty
        };
    }

    /// <summary>
    /// Original custom wire format: <c>$ID:0xNNN,DLC:N,DATA:HEX[,CHK:HH]\r\n</c>.
    /// </summary>
    public string ToCustomString(bool includeChecksum)
    {
        var builder = new StringBuilder(48);
        builder.Append("$ID:0x");
        builder.Append(Identifier.ToString("X3"));
        builder.Append(",DLC:");
        builder.Append(Dlc);
        builder.Append(",DATA:");
        builder.Append(Convert.ToHexString(Payload));

        if (includeChecksum)
        {
            builder.Append(",CHK:");
            builder.Append(CalculateChecksum().ToString("X2"));
        }

        builder.Append("\r\n");
        return builder.ToString();
    }

    /// <summary>
    /// SLCAN/Lawicel standard frame format: <c>tIIIDDD...\r</c>.
    /// Used by ESP32, candleLight and most USB-CAN bridges.
    /// </summary>
    public string ToSlcanString()
    {
        var builder = new StringBuilder(20);
        builder.Append('t');
        builder.Append(Identifier.ToString("X3"));
        builder.Append(Dlc.ToString("X1"));
        builder.Append(Convert.ToHexString(Payload));
        builder.Append('\r');
        return builder.ToString();
    }

    /// <summary>
    /// Raw binary frame. With checksum a trailing XOR byte is appended.
    /// </summary>
    public byte[] ToBinaryFrame(bool includeChecksum)
    {
        var raw = ToRawBinary();
        if (!includeChecksum)
        {
            return raw;
        }

        var withChecksum = new byte[raw.Length + 1];
        raw.CopyTo(withChecksum, 0);
        withChecksum[^1] = CalculateChecksum();
        return withChecksum;
    }
}
