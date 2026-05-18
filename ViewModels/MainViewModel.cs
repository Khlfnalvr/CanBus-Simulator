using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using CanBusSimulator.Configuration;
using CanBusSimulator.Models;
using CanBusSimulator.Serial;
using CanBusSimulator.Simulation;
using CanBusSimulator.Transmission;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace CanBusSimulator.ViewModels;

/// <summary>
/// Coordinates serial connection, simulation source selection, transmission control,
/// and the transmission log for the WinUI 3 main window.
/// </summary>
public sealed partial class MainViewModel : ObservableObject
{
    private const int LogCapacity = 100;

    private readonly ConfigService _configService;
    private readonly AppConfig _config;
    private readonly ISerialTransport _transport;
    private readonly BmsDataSimulator _simulator;
    private readonly FileBmsDataSource _fileDataSource;
    private readonly BmsSnapshotProvider _snapshotProvider;
    private readonly TransmissionService _transmissionService;
    private readonly DispatcherQueue _dispatcher;

    [ObservableProperty]
    private ObservableCollection<SerialPortInfo> _availablePorts = new();

    [ObservableProperty]
    private SerialPortInfo? _selectedPort;

    [ObservableProperty]
    private string _baudRateText = "115200";

    [ObservableProperty]
    private string _connectionStatus = "Disconnected";

    [ObservableProperty]
    private string _connectButtonText = "Connect";

    [ObservableProperty]
    private string _startButtonText = "Start TX";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isTransmitting;

    [ObservableProperty]
    private string _rateText = "TX rate: 0 fps";

    [ObservableProperty]
    private string _statusMessage = "Ready.";

    [ObservableProperty]
    private bool _autoMode = true;

    [ObservableProperty]
    private OperatingScenario _selectedScenario = OperatingScenario.Discharging;

    [ObservableProperty]
    private bool _appendChecksum;

    [ObservableProperty]
    private WireFormat _selectedWireFormat = WireFormat.Custom;

    [ObservableProperty]
    private double _packVoltage = 80.0;

    [ObservableProperty]
    private double _packCurrent = -20.0;

    [ObservableProperty]
    private double _socPercent = 75;

    [ObservableProperty]
    private double _maxTemp = 45;

    [ObservableProperty]
    private double _minTemp = 35;

    [ObservableProperty]
    private double _cellVoltage = 4.106;

    [ObservableProperty]
    private double _balanceMask;

    [ObservableProperty]
    private int _packStatusMs = 100;

    [ObservableProperty]
    private int _cellVoltageMs = 200;

    [ObservableProperty]
    private int _temperatureMs = 500;

    [ObservableProperty]
    private int _balancingMs = 1000;

    [ObservableProperty]
    private int _diagnosticMs = 500;

    [ObservableProperty]
    private int _heartbeatMs = 250;

    [ObservableProperty]
    private string _simulationFilePath = string.Empty;

    [ObservableProperty]
    private bool _useFileData;

    [ObservableProperty]
    private bool _loopFile = true;

    [ObservableProperty]
    private int _fileRowIntervalMs = 1000;

    [ObservableProperty]
    private string _fileInfoText = "No simulation file loaded.";

    [ObservableProperty]
    private string _statsText = "Frames: 0  •  Bytes: 0  •  Errors: 0  •  Uptime: 00:00:00";

    /// <summary>Observable transmission log shown in the UI.</summary>
    public ObservableCollection<LogEntry> Log { get; } = new();

    public IReadOnlyList<OperatingScenario> Scenarios { get; } =
        new[] { OperatingScenario.Idle, OperatingScenario.Charging, OperatingScenario.Discharging };

    public IReadOnlyList<WireFormat> WireFormats { get; } =
        new[] { WireFormat.Custom, WireFormat.Slcan, WireFormat.Binary };

    public MainViewModel(
        ConfigService configService,
        AppConfig config,
        ISerialTransport transport,
        BmsDataSimulator simulator,
        FileBmsDataSource fileDataSource,
        BmsSnapshotProvider snapshotProvider,
        TransmissionService transmissionService)
    {
        _configService = configService;
        _config = config;
        _transport = transport;
        _simulator = simulator;
        _fileDataSource = fileDataSource;
        _snapshotProvider = snapshotProvider;
        _transmissionService = transmissionService;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        LoadFromConfig();
        WireServiceEvents();
        RefreshPorts();
        PushUiToSimulator();
        ApplyIntervals();
    }

    [RelayCommand]
    public void RefreshPorts()
    {
        var current = SelectedPort?.PortName ?? _config.Serial.PortName;
        AvailablePorts.Clear();
        foreach (var port in SerialPortDiscovery.GetAvailablePortInfos())
        {
            AvailablePorts.Add(port);
        }

        SelectedPort = AvailablePorts.FirstOrDefault(port =>
            string.Equals(port.PortName, current, StringComparison.OrdinalIgnoreCase));

        if (SelectedPort is null && !string.IsNullOrWhiteSpace(_config.Serial.PortName))
        {
            var placeholder = new SerialPortInfo(_config.Serial.PortName, "not detected");
            AvailablePorts.Add(placeholder);
            SelectedPort = placeholder;
        }
    }

    [RelayCommand]
    public async Task ToggleConnectionAsync()
    {
        if (_transport.IsOpen)
        {
            if (_transmissionService.IsRunning)
            {
                await _transmissionService.StopAsync();
            }

            _transport.Close();
            UpdateConnectionState();
            return;
        }

        if (!int.TryParse(BaudRateText, out var baudRate) || baudRate <= 0)
        {
            StatusMessage = "Baud rate tidak valid.";
            return;
        }

        var portName = SelectedPort?.PortName ?? string.Empty;
        if (string.IsNullOrWhiteSpace(portName))
        {
            StatusMessage = "Pilih COM port terlebih dahulu.";
            return;
        }

        try
        {
            _config.Serial.PortName = portName;
            _config.Serial.BaudRate = baudRate;
            await _transport.OpenAsync(new SerialOptions(portName, baudRate), CancellationToken.None);
            UpdateConnectionState();
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connect failed: {ex.Message}";
            UpdateConnectionState();
        }
    }

    [RelayCommand]
    public async Task ToggleTransmissionAsync()
    {
        if (_transmissionService.IsRunning)
        {
            await _transmissionService.StopAsync();
            IsTransmitting = false;
            StartButtonText = "Start TX";
            StatusMessage = "Transmission stopped.";
            return;
        }

        if (!_transport.IsOpen)
        {
            await ToggleConnectionAsync();
            if (!_transport.IsOpen) return;
        }

        if (UseFileData && !_fileDataSource.HasRows)
        {
            StatusMessage = "Pilih dan load file simulasi terlebih dahulu.";
            return;
        }

        ApplyIntervals();
        PushUiToSimulator();
        _transmissionService.SetWireFormat(SelectedWireFormat);
        _transmissionService.SetAppendChecksumToWireFormat(AppendChecksum);
        _transmissionService.Start();
        IsTransmitting = true;
        StartButtonText = "Stop TX";
        StatusMessage = _snapshotProvider.UseFileSource && _fileDataSource.HasRows
            ? "Transmission running from file data."
            : "Transmission running from generated data.";
    }

    [RelayCommand]
    public void ClearLog()
    {
        Log.Clear();
        StatusMessage = "Log cleared.";
    }

    [RelayCommand]
    public void ResetStats()
    {
        _transmissionService.ResetStatistics();
        UpdateStatsDisplay();
        StatusMessage = "Statistics reset.";
    }

    /// <summary>
    /// Returns a snapshot of the current log entries serialized as a text export.
    /// </summary>
    public string BuildLogExportText()
    {
        // Snapshot — Log is updated only on the UI thread.
        var entries = Log.ToArray();
        var sb = new StringBuilder(entries.Length * 96);
        sb.AppendLine("Time         | Wire Line                                   | CHK  | Decoded");
        sb.AppendLine("-------------+---------------------------------------------+------+-----------------------------");
        foreach (var e in entries)
        {
            sb.Append(e.Time.PadRight(12)).Append(" | ")
              .Append(e.WireLine.Length > 43 ? e.WireLine[..43] : e.WireLine.PadRight(43)).Append(" | ")
              .Append(e.Checksum.PadRight(4)).Append(" | ")
              .AppendLine(e.Decoded);
        }
        return sb.ToString();
    }

    public async Task LoadSimulationFileAsync(string filePath)
    {
        try
        {
            FileInfoText = "Loading simulation file...";
            var data = await Task.Run(() => SimulationFileReader.Load(filePath, _config.Simulation));
            _fileDataSource.Load(data);
            SimulationFilePath = filePath;
            _config.SimulationFile.LastFilePath = filePath;
            UseFileData = true;
            _snapshotProvider.UseFileSource = true;

            var warningText = data.Warnings.Count == 0 ? string.Empty : $" Warning: {string.Join(" ", data.Warnings)}";
            StatusMessage = $"Loaded {data.Rows.Count} simulation rows.{warningText}";
            UpdateFileInfo();
        }
        catch (Exception ex)
        {
            StatusMessage = $"File load failed: {ex.Message}";
        }
    }

    public void SaveConfig()
    {
        _config.Serial.PortName = SelectedPort?.PortName ?? _config.Serial.PortName;
        if (int.TryParse(BaudRateText, out var baud) && baud > 0)
        {
            _config.Serial.BaudRate = baud;
        }

        _config.Serial.AppendChecksumToWireFormat = AppendChecksum;
        _config.Serial.WireFormat = SelectedWireFormat;
        _config.SimulationFile.LastFilePath = SimulationFilePath;
        _config.SimulationFile.UseFileData = UseFileData;
        _config.SimulationFile.LoopFile = LoopFile;
        _config.SimulationFile.ReplayRowIntervalMs = FileRowIntervalMs;
        _config.Simulation.InitialScenario = SelectedScenario;
        _config.Simulation.DefaultPackVoltageVolts = PackVoltage;
        _config.Simulation.DefaultCurrentAmps = PackCurrent;
        _config.Simulation.DefaultSocPercent = (byte)Math.Clamp((int)Math.Round(SocPercent), 0, 100);
        _config.Simulation.DefaultMaxTemperatureC = (byte)Math.Clamp((int)Math.Round(MaxTemp), 0, 80);
        _config.Simulation.DefaultMinTemperatureC = (byte)Math.Clamp((int)Math.Round(MinTemp), 0, 80);
        _config.Simulation.DefaultCellVoltageVolts = CellVoltage;
        _config.Intervals.PackStatusMs = PackStatusMs;
        _config.Intervals.TemperatureMs = TemperatureMs;
        _config.Intervals.CellVoltageMs = CellVoltageMs;
        _config.Intervals.BalancingMs = BalancingMs;
        _config.Intervals.DiagnosticMs = DiagnosticMs;
        _config.Intervals.HeartbeatMs = HeartbeatMs;
        var error = _configService.Save(_config);
        if (error is not null)
        {
            StatusMessage = $"Config save skipped: {error}";
        }
    }

    public async Task ShutdownAsync()
    {
        await _transmissionService.StopAsync();
        _transport.Close();
        _transmissionService.Dispose();
    }

    public ElementTheme ResolveStartupTheme()
    {
        return _config.Theme switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    public void SetTheme(ElementTheme theme)
    {
        _config.Theme = theme switch
        {
            ElementTheme.Light => "Light",
            ElementTheme.Dark => "Dark",
            _ => "Default"
        };
    }

    partial void OnAutoModeChanged(bool value) => PushUiToSimulator();
    partial void OnSelectedScenarioChanged(OperatingScenario value) => PushUiToSimulator();
    partial void OnPackVoltageChanged(double value) => PushUiToSimulator();
    partial void OnPackCurrentChanged(double value) => PushUiToSimulator();
    partial void OnSocPercentChanged(double value) => PushUiToSimulator();
    partial void OnMaxTempChanged(double value) => PushUiToSimulator();
    partial void OnMinTempChanged(double value) => PushUiToSimulator();
    partial void OnCellVoltageChanged(double value) => PushUiToSimulator();
    partial void OnBalanceMaskChanged(double value) => PushUiToSimulator();

    partial void OnSelectedWireFormatChanged(WireFormat value)
    {
        _transmissionService.SetWireFormat(value);
        _config.Serial.WireFormat = value;
    }

    partial void OnAppendChecksumChanged(bool value)
    {
        _transmissionService.SetAppendChecksumToWireFormat(value);
        _config.Serial.AppendChecksumToWireFormat = value;
    }

    partial void OnUseFileDataChanged(bool value)
    {
        _snapshotProvider.UseFileSource = value;
        _config.SimulationFile.UseFileData = value;
        UpdateFileInfo();
    }

    partial void OnLoopFileChanged(bool value)
    {
        _fileDataSource.LoopFile = value;
        _config.SimulationFile.LoopFile = value;
    }

    partial void OnFileRowIntervalMsChanged(int value)
    {
        _fileDataSource.SetRowInterval(value);
        _config.SimulationFile.ReplayRowIntervalMs = value;
    }

    partial void OnPackStatusMsChanged(int value) => ApplyIntervals();
    partial void OnCellVoltageMsChanged(int value) => ApplyIntervals();
    partial void OnTemperatureMsChanged(int value) => ApplyIntervals();
    partial void OnBalancingMsChanged(int value) => ApplyIntervals();
    partial void OnDiagnosticMsChanged(int value) => ApplyIntervals();
    partial void OnHeartbeatMsChanged(int value) => ApplyIntervals();

    private void LoadFromConfig()
    {
        BaudRateText = _config.Serial.BaudRate.ToString(CultureInfo.InvariantCulture);
        AppendChecksum = _config.Serial.AppendChecksumToWireFormat;
        SelectedWireFormat = _config.Serial.WireFormat;
        SelectedScenario = _config.Simulation.InitialScenario;
        PackVoltage = _config.Simulation.DefaultPackVoltageVolts;
        PackCurrent = _config.Simulation.DefaultCurrentAmps;
        SocPercent = _config.Simulation.DefaultSocPercent;
        MaxTemp = _config.Simulation.DefaultMaxTemperatureC;
        MinTemp = _config.Simulation.DefaultMinTemperatureC;
        CellVoltage = _config.Simulation.DefaultCellVoltageVolts;
        PackStatusMs = _config.Intervals.PackStatusMs;
        TemperatureMs = _config.Intervals.TemperatureMs;
        CellVoltageMs = _config.Intervals.CellVoltageMs;
        BalancingMs = _config.Intervals.BalancingMs;
        DiagnosticMs = _config.Intervals.DiagnosticMs;
        HeartbeatMs = _config.Intervals.HeartbeatMs;
        SimulationFilePath = _config.SimulationFile.LastFilePath;
        UseFileData = _config.SimulationFile.UseFileData;
        LoopFile = _config.SimulationFile.LoopFile;
        FileRowIntervalMs = Math.Max(100, _config.SimulationFile.ReplayRowIntervalMs);
        _fileDataSource.LoopFile = LoopFile;
        _fileDataSource.SetRowInterval(FileRowIntervalMs);
        _snapshotProvider.UseFileSource = UseFileData;
    }

    private void WireServiceEvents()
    {
        _transport.StatusChanged += (_, message) => DispatchUi(() =>
        {
            StatusMessage = message;
            UpdateConnectionState();
        });
        _transmissionService.FrameTransmitted += (_, args) => DispatchUi(() => AddLog(args));
        _transmissionService.Error += (_, message) => DispatchUi(() => StatusMessage = $"Error: {message}");
        _transmissionService.RateUpdated += (_, args) => DispatchUi(() =>
        {
            RateText = $"TX rate: {args.FramesPerSecond:0} fps";
            UpdateStatsDisplay();
            if (_fileDataSource.HasRows) UpdateFileInfo();
        });
    }

    private void PushUiToSimulator()
    {
        _simulator.SetAutoMode(AutoMode);
        _simulator.SetScenario(SelectedScenario);
        _simulator.SetManualInput(new ManualBmsInput(
            PackVoltage,
            PackCurrent,
            (byte)Math.Clamp((int)Math.Round(SocPercent), 0, 100),
            (byte)Math.Clamp((int)Math.Round(MaxTemp), 0, 80),
            (byte)Math.Clamp((int)Math.Round(MinTemp), 0, 80),
            (ushort)Math.Clamp((int)Math.Round(BalanceMask), 0, 65535),
            SelectedScenario,
            CellVoltage));
    }

    private void ApplyIntervals()
    {
        _config.Intervals.PackStatusMs = PackStatusMs;
        _config.Intervals.TemperatureMs = TemperatureMs;
        _config.Intervals.CellVoltageMs = CellVoltageMs;
        _config.Intervals.BalancingMs = BalancingMs;
        _config.Intervals.DiagnosticMs = DiagnosticMs;
        _config.Intervals.HeartbeatMs = HeartbeatMs;
        _transmissionService.UpdateIntervals(_config.Intervals);
    }

    private void UpdateConnectionState()
    {
        IsConnected = _transport.IsOpen;
        ConnectionStatus = IsConnected ? "Connected" : "Disconnected";
        ConnectButtonText = IsConnected ? "Disconnect" : "Connect";
        if (!_transmissionService.IsRunning)
        {
            IsTransmitting = false;
            StartButtonText = "Start TX";
        }
    }

    private void UpdateFileInfo()
    {
        FileInfoText = _fileDataSource.HasRows
            ? $"Loaded {_fileDataSource.RowCount} rows, replay row {_fileDataSource.CurrentRowNumber}/{_fileDataSource.RowCount}."
            : "No simulation file loaded.";
    }

    private void UpdateStatsDisplay()
    {
        var frames = _transmissionService.TotalFrames;
        var bytes = _transmissionService.TotalBytes;
        var errors = _transmissionService.TotalErrors;
        var started = _transmissionService.StartedAt;
        var uptime = started is null ? TimeSpan.Zero : DateTimeOffset.UtcNow - started.Value;
        StatsText = string.Create(CultureInfo.InvariantCulture,
            $"Frames: {frames:N0}  •  Bytes: {bytes:N0}  •  Errors: {errors:N0}  •  Uptime: {uptime:hh\\:mm\\:ss}");
    }

    private void AddLog(FrameTransmittedEventArgs args)
    {
        var entry = new LogEntry(
            args.Timestamp.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture),
            args.WireLine,
            $"0x{args.Checksum:X2}",
            args.DecodedText);

        Log.Insert(0, entry);
        if (Log.Count > LogCapacity)
        {
            Log.RemoveAt(Log.Count - 1);
        }
    }

    private void DispatchUi(Action action)
    {
        if (_dispatcher is null)
        {
            action();
            return;
        }

        if (_dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}
