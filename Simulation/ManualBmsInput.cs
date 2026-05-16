namespace CanBusSimulator.Simulation;

/// <summary>
/// User-controlled values applied when the simulator is in manual mode.
/// </summary>
public sealed record ManualBmsInput(
    double PackVoltageVolts,
    double PackCurrentAmps,
    byte SocPercent,
    byte MaxTemperatureC,
    byte MinTemperatureC,
    ushort ActiveBalanceCells,
    OperatingScenario Scenario,
    double CellVoltageVolts);
