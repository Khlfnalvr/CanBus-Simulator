using System.Globalization;
using CanBusSimulator.Models;
using CanBusSimulator.Simulation;

namespace CanBusSimulator.Can;

/// <summary>
/// Converts BMS snapshots into the CAN frame set consumed by BMS Monitor.
/// Layout matches what an ESP32 master typically forwards from the BMS bus.
/// </summary>
public static class BmsCanFrameFactory
{
    /// <summary>
    /// Message 0x100: pack voltage, pack current, SOC, and status flags.
    /// </summary>
    public static CanFrame CreatePackStatus(BmsSnapshot snapshot, SimulationSettings settings)
    {
        Span<byte> payload = stackalloc byte[8];
        payload[0] = snapshot.Scenario switch
        {
            OperatingScenario.Charging => 1,
            OperatingScenario.Discharging => 2,
            _ => 0
        };
        WriteUInt16BigEndian(payload, 1, ClampToUInt16(snapshot.SocPercent * 10.0));
        WriteInt16BigEndian(payload, 3, ClampToInt16(snapshot.PackCurrentAmps * 10.0));
        WriteUInt16BigEndian(payload, 5, ClampToUInt16(snapshot.PackVoltageVolts * 100.0));
        payload[7] = 0;

        return new CanFrame(0x100, 8, payload);
    }

    /// <summary>
    /// One cell-voltage frame. IDs 0x101-0x105 carry cells 1-20, four cells per frame.
    /// </summary>
    public static CanFrame CreateCellVoltageGroup(BmsSnapshot snapshot, int group)
    {
        if (group is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(group), "Cell group must be 0-4.");
        }

        Span<byte> payload = stackalloc byte[8];
        for (var index = 0; index < 4; index++)
        {
            var cellIndex = (group * 4) + index;
            var volts = cellIndex < snapshot.CellVoltagesVolts.Length ? snapshot.CellVoltagesVolts[cellIndex] : 0.0;
            WriteUInt16BigEndian(payload, index * 2, ClampToUInt16(volts * 1000.0));
        }

        return new CanFrame((ushort)(0x101 + group), 8, payload);
    }

    /// <summary>
    /// One temperature frame. IDs 0x110-0x112 carry ten sensors in 0.1 C units.
    /// </summary>
    public static CanFrame CreateTemperatureGroup(BmsSnapshot snapshot, int group)
    {
        if (group is < 0 or > 2)
        {
            throw new ArgumentOutOfRangeException(nameof(group), "Temperature group must be 0-2.");
        }

        Span<byte> payload = stackalloc byte[8];
        var count = group == 2 ? 2 : 4;
        for (var index = 0; index < count; index++)
        {
            var tempIndex = (group * 4) + index;
            var temp = tempIndex < snapshot.TemperaturesC.Length ? snapshot.TemperaturesC[tempIndex] : 0.0;
            WriteInt16BigEndian(payload, index * 2, ClampToInt16(temp * 10.0));
        }

        return new CanFrame((ushort)(0x110 + group), (byte)(count * 2), payload[..(count * 2)]);
    }

    /// <summary>
    /// Message 0x120: 20 balancing flags in little-endian bit order.
    /// </summary>
    public static CanFrame CreateBalancing(BmsSnapshot snapshot)
    {
        Span<byte> payload = stackalloc byte[3];
        uint bits = 0;
        for (var index = 0; index < Math.Min(20, snapshot.BalanceFlags.Length); index++)
        {
            if (snapshot.BalanceFlags[index])
            {
                bits |= 1u << index;
            }
        }

        payload[0] = (byte)(bits & 0xFF);
        payload[1] = (byte)((bits >> 8) & 0xFF);
        payload[2] = (byte)((bits >> 16) & 0xFF);
        return new CanFrame(0x120, 3, payload);
    }

    /// <summary>
    /// Message 0x130: diagnostic / fault byte map.
    /// byte0=protection bits, byte1=warning bits, byte2=balancing-active count,
    /// byte3=delta cell mV high, byte4=delta cell mV low,
    /// byte5=cycle count high, byte6=cycle count low, byte7=reserved.
    /// </summary>
    public static CanFrame CreateDiagnostic(BmsSnapshot snapshot)
    {
        Span<byte> payload = stackalloc byte[8];

        byte protection = 0;
        byte warning = 0;
        if (snapshot.MaxTemperatureC >= 60) protection |= 0x01;
        else if (snapshot.MaxTemperatureC >= 55) warning |= 0x01;
        if (snapshot.MinTemperatureC <= 5) protection |= 0x02;
        else if (snapshot.MinTemperatureC <= 10) warning |= 0x02;
        if (snapshot.PackVoltageVolts >= 84.0) protection |= 0x04;
        else if (snapshot.PackVoltageVolts >= 83.0) warning |= 0x04;
        if (snapshot.PackVoltageVolts <= 60.0) protection |= 0x08;
        else if (snapshot.PackVoltageVolts <= 62.0) warning |= 0x08;
        if (Math.Abs(snapshot.PackCurrentAmps) >= 100.0) protection |= 0x10;
        else if (Math.Abs(snapshot.PackCurrentAmps) >= 80.0) warning |= 0x10;

        payload[0] = protection;
        payload[1] = warning;

        var balanceCount = 0;
        foreach (var active in snapshot.BalanceFlags)
        {
            if (active) balanceCount++;
        }

        payload[2] = (byte)Math.Min(255, balanceCount);

        var deltaMv = 0;
        if (snapshot.CellVoltagesVolts.Length > 0)
        {
            var max = snapshot.CellVoltagesVolts.Max();
            var min = snapshot.CellVoltagesVolts.Min();
            deltaMv = (int)Math.Round((max - min) * 1000.0);
        }

        WriteUInt16BigEndian(payload, 3, ClampToUInt16(deltaMv));

        var cycleCount = snapshot.CycleCount;
        WriteUInt16BigEndian(payload, 5, ClampToUInt16(cycleCount));
        payload[7] = 0;

        return new CanFrame(0x130, 8, payload);
    }

    /// <summary>
    /// Message 0x140: heartbeat. Carries firmware id, counter, and uptime seconds.
    /// </summary>
    public static CanFrame CreateHeartbeat(BmsSnapshot snapshot, byte counter)
    {
        Span<byte> payload = stackalloc byte[8];
        // 'B','M','S','M' = BMS Master signature so monitor can detect the ESP source
        payload[0] = (byte)'B';
        payload[1] = (byte)'M';
        payload[2] = (byte)'S';
        payload[3] = (byte)'M';
        payload[4] = counter;

        var uptimeSeconds = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() & 0xFFFFFF);
        payload[5] = (byte)((uptimeSeconds >> 16) & 0xFF);
        payload[6] = (byte)((uptimeSeconds >> 8) & 0xFF);
        payload[7] = (byte)(uptimeSeconds & 0xFF);

        return new CanFrame(0x140, 8, payload);
    }

    /// <summary>
    /// Returns decoded text for logs and troubleshooting.
    /// </summary>
    public static string Describe(CanFrame frame, SimulationSettings settings)
    {
        return frame.Identifier switch
        {
            0x100 when frame.Payload.Length == 8 => DescribePackStatus(frame.Payload, settings),
            >= 0x101 and <= 0x105 when frame.Payload.Length == 8 => DescribeCells(frame),
            >= 0x110 and <= 0x112 => DescribeTemperature(frame),
            0x120 => DescribeBalancing(frame.Payload),
            0x130 when frame.Payload.Length == 8 => DescribeDiagnostic(frame.Payload),
            0x140 when frame.Payload.Length == 8 => DescribeHeartbeat(frame.Payload),
            _ => "Unknown frame"
        };
    }

    private static string DescribePackStatus(byte[] payload, SimulationSettings settings)
    {
        var status = payload[0] switch
        {
            1 => "Charging",
            2 => "Discharging",
            3 => "Fault",
            _ => "Idle"
        };
        var soc = ReadUInt16BigEndian(payload, 1) / 10.0;
        var current = ReadInt16BigEndian(payload, 3) / 10.0;
        var voltage = ReadUInt16BigEndian(payload, 5) / 100.0;
        return string.Create(CultureInfo.InvariantCulture, $"Pack={voltage:0.00} V, Current={current:0.0} A, SOC={soc:0.0}%, Status={status}");
    }

    private static string DescribeTemperature(CanFrame frame)
    {
        var first = ((frame.Identifier - 0x110) * 4) + 1;
        var count = frame.Identifier == 0x112 ? 2 : 4;
        var temps = new string[count];
        for (var index = 0; index < count; index++)
        {
            temps[index] = $"T{first + index}={ReadInt16BigEndian(frame.Payload, index * 2) / 10.0:0.0} C";
        }

        return string.Join(", ", temps);
    }

    private static string DescribeCells(CanFrame frame)
    {
        var first = ((frame.Identifier - 0x101) * 4) + 1;
        var cells = new string[4];
        for (var index = 0; index < cells.Length; index++)
        {
            cells[index] = $"C{first + index}={ReadUInt16BigEndian(frame.Payload, index * 2)} mV";
        }

        return string.Join(", ", cells);
    }

    private static string DescribeBalancing(byte[] payload)
    {
        var bits = payload.Length == 0 ? 0u : payload[0];
        if (payload.Length > 1) bits |= (uint)payload[1] << 8;
        if (payload.Length > 2) bits |= (uint)payload[2] << 16;
        return $"BalanceBits=0x{bits:X5}";
    }

    private static string DescribeDiagnostic(byte[] payload)
    {
        var protection = payload[0];
        var warning = payload[1];
        var balCount = payload[2];
        var delta = ReadUInt16BigEndian(payload, 3);
        var cycles = ReadUInt16BigEndian(payload, 5);
        return $"Prot=0x{protection:X2}, Warn=0x{warning:X2}, BalActive={balCount}, dV={delta}mV, Cycles={cycles}";
    }

    private static string DescribeHeartbeat(byte[] payload)
    {
        var sig = System.Text.Encoding.ASCII.GetString(payload, 0, 4);
        var counter = payload[4];
        var uptime = ((uint)payload[5] << 16) | ((uint)payload[6] << 8) | payload[7];
        return $"Sig={sig}, Cnt={counter}, Up={uptime}s";
    }

    private static void WriteUInt16BigEndian(Span<byte> payload, int offset, ushort value)
    {
        payload[offset] = (byte)(value >> 8);
        payload[offset + 1] = (byte)(value & 0xFF);
    }

    private static void WriteInt16BigEndian(Span<byte> payload, int offset, short value)
    {
        payload[offset] = (byte)((ushort)value >> 8);
        payload[offset + 1] = (byte)(value & 0xFF);
    }

    private static ushort ReadUInt16BigEndian(byte[] payload, int offset)
    {
        return (ushort)((payload[offset] << 8) | payload[offset + 1]);
    }

    private static short ReadInt16BigEndian(byte[] payload, int offset)
    {
        return unchecked((short)ReadUInt16BigEndian(payload, offset));
    }

    private static ushort ClampToUInt16(double value)
    {
        return (ushort)Math.Clamp((int)Math.Round(value), ushort.MinValue, ushort.MaxValue);
    }

    private static ushort ClampToUInt16(int value)
    {
        return (ushort)Math.Clamp(value, ushort.MinValue, ushort.MaxValue);
    }

    private static short ClampToInt16(double value)
    {
        return (short)Math.Clamp((int)Math.Round(value), short.MinValue, short.MaxValue);
    }
}
