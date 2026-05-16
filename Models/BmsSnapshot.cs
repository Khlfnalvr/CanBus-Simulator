using CanBusSimulator.Simulation;

namespace CanBusSimulator.Models;

/// <summary>
/// Immutable snapshot of the simulated BMS state used to generate CAN frames.
/// </summary>
public sealed record BmsSnapshot(
    double PackVoltageVolts,
    double PackCurrentAmps,
    byte SocPercent,
    byte MaxTemperatureC,
    byte MinTemperatureC,
    ushort ActiveBalanceCells,
    OperatingScenario Scenario,
    double[] CellVoltagesVolts,
    double[] TemperaturesC,
    bool[] BalanceFlags,
    DateTimeOffset Timestamp);
