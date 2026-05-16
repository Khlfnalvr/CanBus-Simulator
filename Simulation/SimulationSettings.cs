namespace CanBusSimulator.Simulation;

/// <summary>
/// Runtime defaults, physical bounds, and scaling values for BMS simulation.
/// </summary>
public sealed class SimulationSettings
{
    /// <summary>
    /// Scenario selected on startup.
    /// </summary>
    public OperatingScenario InitialScenario { get; set; } = OperatingScenario.Discharging;

    /// <summary>
    /// Nominal pack capacity used to estimate gradual SOC movement.
    /// </summary>
    public double NominalCapacityAh { get; set; } = 120.0;

    /// <summary>
    /// Pack voltage scale used for ID 0x100. Default 20 mV/bit maps 80 V to 0x0FA0.
    /// </summary>
    public double PackVoltageScaleMillivoltsPerBit { get; set; } = 20.0;

    /// <summary>
    /// Current scale used for ID 0x100. Default 1 A/bit maps -20 A to 0xFFEC.
    /// </summary>
    public double CurrentScaleAmpsPerBit { get; set; } = 1.0;

    /// <summary>
    /// Default pack voltage shown in manual mode.
    /// </summary>
    public double DefaultPackVoltageVolts { get; set; } = 80.0;

    /// <summary>
    /// Default pack current shown in manual mode.
    /// </summary>
    public double DefaultCurrentAmps { get; set; } = -20.0;

    /// <summary>
    /// Default state of charge shown in manual mode.
    /// </summary>
    public byte DefaultSocPercent { get; set; } = 75;

    /// <summary>
    /// Default maximum temperature shown in manual mode.
    /// </summary>
    public byte DefaultMaxTemperatureC { get; set; } = 45;

    /// <summary>
    /// Default minimum temperature shown in manual mode.
    /// </summary>
    public byte DefaultMinTemperatureC { get; set; } = 35;

    /// <summary>
    /// Default cell voltage used for message 0x102 in manual mode.
    /// </summary>
    public double DefaultCellVoltageVolts { get; set; } = 4.106;
}
