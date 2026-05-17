using System.Globalization;
using System.Runtime.CompilerServices;
using CanBusSimulator.Models;
using CanBusSimulator.Simulation;

namespace CanBusSimulator.Can;

/// <summary>
/// Converts BMS snapshots into the CAN frame set consumed by BMS Monitor.
/// Layout matches what an ESP32 master typically forwards from the BMS bus.
/// </summary>
public static class BmsCanFrameFactory
{
    public static CanFrame CreatePackStatus(BmsSnapshot snapshot, SimulationSettings settings)
    {
        Span<byte> payload = stackalloc byte[8];
        payload[0] = snapshot.Scenario switch
        {
            OperatingScenario.Charging => 1,
            OperatingScenario.Discharging => 2,
            _ => 0
        };
        WriteUInt16BE(payload, 1, ClampU16(snapshot.SocPercent * 10.0));
        WriteInt16BE(payload, 3, ClampI16(snapshot.PackCurrentAmps * 10.0));
        WriteUInt16BE(payload, 5, ClampU16(snapshot.PackVoltageVolts * 100.0));
        payload[7] = 0;
        return new CanFrame(0x100, 8, payload);
    }

    public static CanFrame CreateCellVoltageGroup(BmsSnapshot snapshot, int group)
    {
        if ((uint)group > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(group), "Cell group must be 0-4.");
        }

        Span<byte> payload = stackalloc byte[8];
        var cells = snapshot.CellVoltagesVolts;
        var baseIdx = group * 4;
        for (var i = 0; i < 4; i++)
        {
            var idx = baseIdx + i;
            var v = idx < cells.Length ? cells[idx] : 0.0;
            WriteUInt16BE(payload, i * 2, ClampU16(v * 1000.0));
        }

        return new CanFrame((ushort)(0x101 + group), 8, payload);
    }

    public static CanFrame CreateTemperatureGroup(BmsSnapshot snapshot, int group)
    {
        if ((uint)group > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(group), "Temperature group must be 0-2.");
        }

        Span<byte> payload = stackalloc byte[8];
        var temps = snapshot.TemperaturesC;
        var count = group == 2 ? 2 : 4;
        var baseIdx = group * 4;
        for (var i = 0; i < count; i++)
        {
            var idx = baseIdx + i;
            var t = idx < temps.Length ? temps[idx] : 0.0;
            WriteInt16BE(payload, i * 2, ClampI16(t * 10.0));
        }

        return new CanFrame((ushort)(0x110 + group), (byte)(count * 2), payload[..(count * 2)]);
    }

    public static CanFrame CreateBalancing(BmsSnapshot snapshot)
    {
        Span<byte> payload = stackalloc byte[3];
        var flags = snapshot.BalanceFlags;
        var n = Math.Min(20, flags.Length);
        uint bits = 0;
        for (var i = 0; i < n; i++)
        {
            if (flags[i]) bits |= 1u << i;
        }

        payload[0] = (byte)(bits & 0xFF);
        payload[1] = (byte)((bits >> 8) & 0xFF);
        payload[2] = (byte)((bits >> 16) & 0xFF);
        return new CanFrame(0x120, 3, payload);
    }

    public static CanFrame CreateDiagnostic(BmsSnapshot snapshot)
    {
        Span<byte> payload = stackalloc byte[8];

        byte protection = 0, warning = 0;
        if (snapshot.MaxTemperatureC >= 60) protection |= 0x01;
        else if (snapshot.MaxTemperatureC >= 55) warning |= 0x01;
        if (snapshot.MinTemperatureC <= 5) protection |= 0x02;
        else if (snapshot.MinTemperatureC <= 10) warning |= 0x02;
        if (snapshot.PackVoltageVolts >= 84.0) protection |= 0x04;
        else if (snapshot.PackVoltageVolts >= 83.0) warning |= 0x04;
        if (snapshot.PackVoltageVolts <= 60.0) protection |= 0x08;
        else if (snapshot.PackVoltageVolts <= 62.0) warning |= 0x08;
        var absI = Math.Abs(snapshot.PackCurrentAmps);
        if (absI >= 100.0) protection |= 0x10;
        else if (absI >= 80.0) warning |= 0x10;

        payload[0] = protection;
        payload[1] = warning;

        var flags = snapshot.BalanceFlags;
        var balanceCount = 0;
        for (var i = 0; i < flags.Length; i++)
        {
            if (flags[i]) balanceCount++;
        }
        payload[2] = (byte)Math.Min(255, balanceCount);

        var deltaMv = 0;
        var cells = snapshot.CellVoltagesVolts;
        if (cells.Length > 0)
        {
            double max = cells[0], min = cells[0];
            for (var i = 1; i < cells.Length; i++)
            {
                var v = cells[i];
                if (v > max) max = v;
                else if (v < min) min = v;
            }
            deltaMv = (int)Math.Round((max - min) * 1000.0);
        }

        WriteUInt16BE(payload, 3, ClampU16(deltaMv));
        WriteUInt16BE(payload, 5, ClampU16(snapshot.CycleCount));
        payload[7] = 0;

        return new CanFrame(0x130, 8, payload);
    }

    public static CanFrame CreateHeartbeat(BmsSnapshot snapshot, byte counter)
    {
        Span<byte> payload = stackalloc byte[8];
        // 'B','M','S','M' = BMS Master signature
        payload[0] = (byte)'B';
        payload[1] = (byte)'M';
        payload[2] = (byte)'S';
        payload[3] = (byte)'M';
        payload[4] = counter;

        var uptime = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0xFFFFFF);
        payload[5] = (byte)((uptime >> 16) & 0xFF);
        payload[6] = (byte)((uptime >> 8) & 0xFF);
        payload[7] = (byte)(uptime & 0xFF);

        return new CanFrame(0x140, 8, payload);
    }

    /// <summary>
    /// Returns decoded text for logs and troubleshooting.
    /// </summary>
    public static string Describe(CanFrame frame, SimulationSettings settings)
    {
        _ = settings;
        var p = frame.Payload;
        return frame.Identifier switch
        {
            0x100 when p.Length == 8 => DescribePackStatus(p),
            >= 0x101 and <= 0x105 when p.Length == 8 => DescribeCells(frame),
            >= 0x110 and <= 0x112 => DescribeTemperature(frame),
            0x120 => DescribeBalancing(p),
            0x130 when p.Length == 8 => DescribeDiagnostic(p),
            0x140 when p.Length == 8 => DescribeHeartbeat(p),
            _ => "Unknown frame"
        };
    }

    private static string DescribePackStatus(byte[] p)
    {
        var status = p[0] switch
        {
            1 => "Charging",
            2 => "Discharging",
            3 => "Fault",
            _ => "Idle"
        };
        var soc = ReadU16BE(p, 1) / 10.0;
        var current = ReadI16BE(p, 3) / 10.0;
        var voltage = ReadU16BE(p, 5) / 100.0;
        return string.Create(CultureInfo.InvariantCulture,
            $"Pack={voltage:0.00} V, Current={current:0.0} A, SOC={soc:0.0}%, Status={status}");
    }

    private static string DescribeTemperature(CanFrame frame)
    {
        var first = ((frame.Identifier - 0x110) * 4) + 1;
        var count = frame.Identifier == 0x112 ? 2 : 4;
        var p = frame.Payload;
        var inv = CultureInfo.InvariantCulture;
        return count switch
        {
            2 => string.Create(inv,
                $"T{first}={ReadI16BE(p, 0) / 10.0:0.0} C, T{first + 1}={ReadI16BE(p, 2) / 10.0:0.0} C"),
            _ => string.Create(inv,
                $"T{first}={ReadI16BE(p, 0) / 10.0:0.0} C, T{first + 1}={ReadI16BE(p, 2) / 10.0:0.0} C, T{first + 2}={ReadI16BE(p, 4) / 10.0:0.0} C, T{first + 3}={ReadI16BE(p, 6) / 10.0:0.0} C")
        };
    }

    private static string DescribeCells(CanFrame frame)
    {
        var first = ((frame.Identifier - 0x101) * 4) + 1;
        var p = frame.Payload;
        var inv = CultureInfo.InvariantCulture;
        return string.Create(inv,
            $"C{first}={ReadU16BE(p, 0)} mV, C{first + 1}={ReadU16BE(p, 2)} mV, C{first + 2}={ReadU16BE(p, 4)} mV, C{first + 3}={ReadU16BE(p, 6)} mV");
    }

    private static string DescribeBalancing(byte[] p)
    {
        var bits = p.Length == 0 ? 0u : p[0];
        if (p.Length > 1) bits |= (uint)p[1] << 8;
        if (p.Length > 2) bits |= (uint)p[2] << 16;
        return $"BalanceBits=0x{bits:X5}";
    }

    private static string DescribeDiagnostic(byte[] p)
    {
        return $"Prot=0x{p[0]:X2}, Warn=0x{p[1]:X2}, BalActive={p[2]}, dV={ReadU16BE(p, 3)}mV, Cycles={ReadU16BE(p, 5)}";
    }

    private static string DescribeHeartbeat(byte[] p)
    {
        var sig = System.Text.Encoding.ASCII.GetString(p, 0, 4);
        var counter = p[4];
        var uptime = ((uint)p[5] << 16) | ((uint)p[6] << 8) | p[7];
        return $"Sig={sig}, Cnt={counter}, Up={uptime}s";
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteUInt16BE(Span<byte> p, int o, ushort v)
    {
        p[o] = (byte)(v >> 8);
        p[o + 1] = (byte)(v & 0xFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void WriteInt16BE(Span<byte> p, int o, short v)
    {
        p[o] = (byte)((ushort)v >> 8);
        p[o + 1] = (byte)(v & 0xFF);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ReadU16BE(byte[] p, int o) => (ushort)((p[o] << 8) | p[o + 1]);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short ReadI16BE(byte[] p, int o) => unchecked((short)ReadU16BE(p, o));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ClampU16(double v) =>
        (ushort)Math.Clamp((int)Math.Round(v), ushort.MinValue, ushort.MaxValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static ushort ClampU16(int v) =>
        (ushort)Math.Clamp(v, ushort.MinValue, ushort.MaxValue);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static short ClampI16(double v) =>
        (short)Math.Clamp((int)Math.Round(v), short.MinValue, short.MaxValue);
}
