using System.Runtime.CompilerServices;
using System.Text;

namespace CanBusSimulator.Models;

/// <summary>
/// Represents a CAN 2.0B standard frame with an 11-bit identifier.
/// </summary>
public sealed class CanFrame
{
    private static ReadOnlySpan<byte> HexAscii =>
        "0123456789ABCDEF"u8;

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

    public ushort Identifier { get; }

    public byte Dlc { get; }

    public byte[] Payload { get; }

    public static CanFrame FromPayload(ushort identifier, ReadOnlySpan<byte> payload)
    {
        return new CanFrame(identifier, (byte)payload.Length, payload);
    }

    /// <summary>
    /// XOR checksum over [ID_HI][ID_LO][DLC][payload].
    /// </summary>
    public byte CalculateChecksum()
    {
        byte c = (byte)((Identifier >> 8) ^ (Identifier & 0xFF) ^ Dlc);
        var data = Payload;
        for (var i = 0; i < data.Length; i++)
        {
            c ^= data[i];
        }
        return c;
    }

    /// <summary>
    /// Renders into <paramref name="destination"/>. Returns the number of bytes written.
    /// Caller must size destination at least 64 bytes to fit any format.
    /// </summary>
    public int TryRender(Span<byte> destination, WireFormat format, bool includeChecksum)
    {
        return format switch
        {
            WireFormat.Custom => WriteCustom(destination, includeChecksum),
            WireFormat.Slcan => WriteSlcan(destination),
            WireFormat.Binary => WriteBinary(destination, includeChecksum),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unknown wire format.")
        };
    }

    /// <summary>Returns rendered bytes. Convenience overload that allocates.</summary>
    public byte[] Render(WireFormat format, bool includeChecksum)
    {
        Span<byte> buffer = stackalloc byte[64];
        var written = TryRender(buffer, format, includeChecksum);
        return buffer[..written].ToArray();
    }

    /// <summary>Human-readable line for the UI log.</summary>
    public string ToDisplayString(WireFormat format, bool includeChecksum)
    {
        Span<byte> buffer = stackalloc byte[64];
        var written = TryRender(buffer, format, includeChecksum);
        var slice = buffer[..written];

        return format switch
        {
            WireFormat.Custom => Encoding.ASCII.GetString(TrimTrailing(slice, (byte)'\r', (byte)'\n')),
            WireFormat.Slcan => Encoding.ASCII.GetString(TrimTrailing(slice, (byte)'\r')),
            WireFormat.Binary => "BIN " + Convert.ToHexString(slice),
            _ => string.Empty
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> TrimTrailing(ReadOnlySpan<byte> s, byte a)
    {
        while (s.Length > 0 && s[^1] == a) s = s[..^1];
        return s;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ReadOnlySpan<byte> TrimTrailing(ReadOnlySpan<byte> s, byte a, byte b)
    {
        while (s.Length > 0 && (s[^1] == a || s[^1] == b)) s = s[..^1];
        return s;
    }

    private int WriteCustom(Span<byte> dst, bool includeChecksum)
    {
        // "$ID:0xNNN,DLC:N,DATA:HEX[,CHK:HH]\r\n"
        var pos = 0;
        WriteAscii(dst, ref pos, "$ID:0x"u8);
        WriteHex(dst, ref pos, (byte)(Identifier >> 8), upperByte: true);
        WriteHex(dst, ref pos, (byte)(Identifier & 0xFF), upperByte: false);
        WriteAscii(dst, ref pos, ",DLC:"u8);
        dst[pos++] = (byte)('0' + Dlc);
        WriteAscii(dst, ref pos, ",DATA:"u8);
        var payload = Payload;
        for (var i = 0; i < payload.Length; i++) WriteHex(dst, ref pos, payload[i]);

        if (includeChecksum)
        {
            WriteAscii(dst, ref pos, ",CHK:"u8);
            WriteHex(dst, ref pos, CalculateChecksum());
        }

        dst[pos++] = (byte)'\r';
        dst[pos++] = (byte)'\n';
        return pos;
    }

    private int WriteSlcan(Span<byte> dst)
    {
        // "tIIIDHEX\r" — checksum ignored for SLCAN.
        var pos = 0;
        dst[pos++] = (byte)'t';
        WriteHex(dst, ref pos, (byte)(Identifier >> 8), upperByte: true);
        WriteHex(dst, ref pos, (byte)(Identifier & 0xFF), upperByte: false);
        dst[pos++] = HexAscii[Dlc & 0x0F];
        var payload = Payload;
        for (var i = 0; i < payload.Length; i++) WriteHex(dst, ref pos, payload[i]);
        dst[pos++] = (byte)'\r';
        return pos;
    }

    private int WriteBinary(Span<byte> dst, bool includeChecksum)
    {
        dst[0] = (byte)(Identifier >> 8);
        dst[1] = (byte)(Identifier & 0xFF);
        dst[2] = Dlc;
        Payload.CopyTo(dst[3..]);
        var len = 3 + Payload.Length;
        if (includeChecksum)
        {
            dst[len++] = CalculateChecksum();
        }
        return len;
    }

    // 3-nibble ID: writes high byte as one nibble ("X3" → first hex char), low byte as two.
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteHex(Span<byte> dst, ref int pos, byte value, bool upperByte = false)
    {
        if (upperByte)
        {
            dst[pos++] = HexAscii[value & 0x0F]; // single nibble for the top of the 11-bit id
        }
        else
        {
            dst[pos++] = HexAscii[(value >> 4) & 0x0F];
            dst[pos++] = HexAscii[value & 0x0F];
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteAscii(Span<byte> dst, ref int pos, ReadOnlySpan<byte> literal)
    {
        literal.CopyTo(dst[pos..]);
        pos += literal.Length;
    }
}
