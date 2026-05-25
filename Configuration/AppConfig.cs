using CanBusSimulator.Simulation;
using CanBusSimulator.Transmission;

namespace CanBusSimulator.Configuration;

/// <summary>
/// Root configuration for the simulator application.
/// </summary>
public sealed class AppConfig
{
    /// <summary>Window theme override. "Default" follows the Windows system theme.</summary>
    public string Theme { get; set; } = "Default";

    /// <summary>Serial connection settings used by the COM writer.</summary>
    public SerialConfig Serial { get; set; } = new();

    /// <summary>JSON-snapshot transmission cadence.</summary>
    public TransmissionIntervals Intervals { get; set; } = new();

    /// <summary>Runtime defaults and scaling factors for generated BMS data.</summary>
    public SimulationSettings Simulation { get; set; } = new();

    /// <summary>Optional CSV/XLSX replay settings for file-driven simulation.</summary>
    public SimulationFileConfig SimulationFile { get; set; } = new();
}

/// <summary>
/// Configuration values for opening the Windows COM port.
/// </summary>
public sealed class SerialConfig
{
    /// <summary>Target COM port name, for example COM5.</summary>
    public string PortName { get; set; } = "COM5";

    /// <summary>UART baud rate. The default matches the ESP32 firmware note.</summary>
    public int BaudRate { get; set; } = 115200;
}

/// <summary>
/// Settings for replaying BMS snapshots from CSV or Excel files.
/// </summary>
public sealed class SimulationFileConfig
{
    /// <summary>Last loaded simulation file path.</summary>
    public string LastFilePath { get; set; } = string.Empty;

    /// <summary>When true and a file is loaded, transmission uses file rows instead of generated values.</summary>
    public bool UseFileData { get; set; }

    /// <summary>When true, replay returns to the first row after the final row.</summary>
    public bool LoopFile { get; set; } = true;

    /// <summary>Delay between advancing data rows during replay.</summary>
    public int ReplayRowIntervalMs { get; set; } = 1000;
}
