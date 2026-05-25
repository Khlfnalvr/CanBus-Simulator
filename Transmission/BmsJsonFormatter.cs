using System.Globalization;
using System.Text;
using CanBusSimulator.Models;
using CanBusSimulator.Simulation;

namespace CanBusSimulator.Transmission;

/// <summary>
/// Serializes a <see cref="BmsSnapshot"/> into the JSON line consumed by BMS Monitor:
/// <c>{"v":53.12,"i":-2.5,"soc":78,"st":"discharging","cells":[…20 floats…],"temps":[…10 ints…],"bal":[0,5,12]}\n</c>.
/// </summary>
public static class BmsJsonFormatter
{
    /// <summary>Appends the JSON line (terminated with <c>\n</c>) for the given snapshot.</summary>
    public static void Format(BmsSnapshot snapshot, StringBuilder destination)
    {
        var inv = CultureInfo.InvariantCulture;

        destination.Append("{\"v\":");
        destination.Append(snapshot.PackVoltageVolts.ToString("0.00", inv));
        destination.Append(",\"i\":");
        destination.Append(snapshot.PackCurrentAmps.ToString("0.0", inv));
        destination.Append(",\"soc\":");
        destination.Append(snapshot.SocPercent);
        destination.Append(",\"st\":\"");
        destination.Append(StatusLabel(snapshot.Scenario));
        destination.Append("\",\"cells\":[");

        var cells = snapshot.CellVoltagesVolts;
        for (var i = 0; i < cells.Length; i++)
        {
            if (i > 0) destination.Append(',');
            destination.Append(cells[i].ToString("0.000", inv));
        }

        destination.Append("],\"temps\":[");
        var temps = snapshot.TemperaturesC;
        for (var i = 0; i < temps.Length; i++)
        {
            if (i > 0) destination.Append(',');
            destination.Append((int)Math.Round(temps[i]));
        }

        destination.Append("],\"bal\":[");
        var flags = snapshot.BalanceFlags;
        var first = true;
        for (var i = 0; i < flags.Length; i++)
        {
            if (!flags[i]) continue;
            if (!first) destination.Append(',');
            destination.Append(i);
            first = false;
        }

        destination.Append("]}\n");
    }

    /// <summary>Returns a one-line human-readable summary for the UI log.</summary>
    public static string Describe(BmsSnapshot snapshot)
    {
        var inv = CultureInfo.InvariantCulture;
        var balanceCount = 0;
        var flags = snapshot.BalanceFlags;
        for (var i = 0; i < flags.Length; i++)
        {
            if (flags[i]) balanceCount++;
        }

        return string.Create(inv,
            $"Pack={snapshot.PackVoltageVolts:0.00} V, I={snapshot.PackCurrentAmps:0.0} A, SOC={snapshot.SocPercent}%, {StatusLabel(snapshot.Scenario)}, Bal={balanceCount}");
    }

    private static string StatusLabel(OperatingScenario scenario) => scenario switch
    {
        OperatingScenario.Charging    => "charging",
        OperatingScenario.Discharging => "discharging",
        _                             => "idle",
    };
}
