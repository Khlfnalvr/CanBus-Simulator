using CanBusSimulator.Configuration;
using CanBusSimulator.Serial;
using CanBusSimulator.Simulation;
using CanBusSimulator.Transmission;

namespace CanBusSimulator.UI;

/// <summary>
/// Main Windows Forms UI for connecting, configuring, and observing CAN simulation.
/// </summary>
public sealed class MainForm : Form
{
    private readonly ConfigService _configService;
    private readonly AppConfig _config;
    private readonly ISerialTransport _transport;
    private readonly BmsDataSimulator _simulator;
    private readonly FileBmsDataSource _fileDataSource;
    private readonly BmsSnapshotProvider _snapshotProvider;
    private readonly TransmissionService _transmissionService;
    private readonly ComboBox _portCombo = new() { DropDownStyle = ComboBoxStyle.DropDown };
    private readonly TextBox _baudText = new() { Width = 90 };
    private readonly Button _refreshButton = new() { Text = "Refresh", AutoSize = true };
    private readonly Button _connectButton = new() { Text = "Connect", AutoSize = true };
    private readonly Button _startButton = new() { Text = "Start TX", AutoSize = true, Enabled = false };
    private readonly Label _connectionLabel = new() { AutoSize = true, Text = "Disconnected" };
    private readonly Label _rateLabel = new() { AutoSize = true, Text = "TX rate: 0 fps" };
    private readonly Label _queueLabel = new() { AutoSize = true, Text = "Ready" };
    private readonly ComboBox _scenarioCombo = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox _autoModeCheck = new() { Text = "Auto simulation", Checked = true, AutoSize = true };
    private readonly CheckBox _checksumCheck = new() { Text = "Append CHK to wire format", AutoSize = true };
    private readonly TextBox _filePathText = new() { ReadOnly = true };
    private readonly Button _browseFileButton = new() { Text = "Browse...", AutoSize = true };
    private readonly CheckBox _useFileDataCheck = new() { Text = "Use file data", AutoSize = true };
    private readonly CheckBox _loopFileCheck = new() { Text = "Loop", AutoSize = true };
    private readonly NumericUpDown _fileRowIntervalInput = CreateNumeric(1000, 60000, 0, 1000m);
    private readonly Label _fileInfoLabel = new() { AutoSize = true, Text = "No simulation file loaded." };
    private readonly System.Windows.Forms.Timer _fileInfoTimer = new() { Interval = 250 };
    private readonly NumericUpDown _packVoltageInput = CreateNumeric(70, 84, 2, 0.1m);
    private readonly NumericUpDown _currentInput = CreateNumeric(-50, 50, 1, 0.5m);
    private readonly NumericUpDown _socInput = CreateNumeric(0, 100, 0, 1m);
    private readonly NumericUpDown _maxTempInput = CreateNumeric(25, 65, 0, 1m);
    private readonly NumericUpDown _minTempInput = CreateNumeric(25, 65, 0, 1m);
    private readonly NumericUpDown _cellVoltageInput = CreateNumeric(3.0m, 4.2m, 3, 0.001m);
    private readonly NumericUpDown _balanceMaskInput = CreateNumeric(0, 65535, 0, 1m);
    private readonly NumericUpDown _packIntervalInput = CreateNumeric(1000, 60000, 0, 1000m);
    private readonly NumericUpDown _temperatureIntervalInput = CreateNumeric(1000, 60000, 0, 1000m);
    private readonly NumericUpDown _cellIntervalInput = CreateNumeric(1000, 60000, 0, 1000m);
    private readonly ListBox _logList = new() { Dock = DockStyle.Fill, HorizontalScrollbar = true };
    private SerialPortInfo? _selectedPortInfo;

    /// <summary>
    /// Creates the UI and wires serial, simulation, and transmission services.
    /// </summary>
    public MainForm(ConfigService configService, AppConfig config, ISerialTransport transport)
    {
        _configService = configService;
        _config = config;
        _transport = transport;
        _simulator = new BmsDataSimulator(config.Simulation);
        _fileDataSource = new FileBmsDataSource();
        _snapshotProvider = new BmsSnapshotProvider(_simulator, _fileDataSource);
        _transmissionService = new TransmissionService(
            _transport,
            _snapshotProvider,
            _config.Simulation,
            _config.Intervals,
            _config.Serial.AppendChecksumToWireFormat);

        Text = "CAN Bus BMS Simulator";
        MinimumSize = new Size(920, 650);
        Size = new Size(1040, 740);
        StartPosition = FormStartPosition.CenterScreen;

        BuildUi();
        BindEvents();
        LoadSettingsIntoUi();
        RefreshPorts();
        PushUiToSimulator();
        UpdateConnectionUi();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 6,
            Padding = new Padding(12)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        root.Controls.Add(CreateConnectionGroup(), 0, 0);
        root.Controls.Add(CreateSimulationGroup(), 0, 1);
        root.Controls.Add(CreateFileReplayGroup(), 0, 2);
        root.Controls.Add(CreateTimingGroup(), 0, 3);
        root.Controls.Add(CreateLogGroup(), 0, 4);
        root.Controls.Add(CreateStatusPanel(), 0, 5);
        Controls.Add(root);
    }

    private GroupBox CreateConnectionGroup()
    {
        var group = new GroupBox { Text = "Serial Port Connection", Dock = DockStyle.Top, AutoSize = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, AutoSize = true, Padding = new Padding(10) };
        for (var i = 0; i < 8; i++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(i is 1 or 3 ? SizeType.Percent : SizeType.AutoSize, i is 1 or 3 ? 50 : 0));
        }

        layout.Controls.Add(new Label { Text = "COM port", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_portCombo, 1, 0);
        layout.Controls.Add(_refreshButton, 2, 0);
        layout.Controls.Add(new Label { Text = "Baud", AutoSize = true, Anchor = AnchorStyles.Left }, 3, 0);
        layout.Controls.Add(_baudText, 4, 0);
        layout.Controls.Add(_connectButton, 5, 0);
        layout.Controls.Add(_startButton, 6, 0);
        layout.Controls.Add(_connectionLabel, 7, 0);
        _portCombo.Dock = DockStyle.Fill;
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox CreateSimulationGroup()
    {
        var group = new GroupBox { Text = "Simulation Parameters", Dock = DockStyle.Top, AutoSize = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, AutoSize = true, Padding = new Padding(10) };
        for (var i = 0; i < 8; i++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f));
        }

        _scenarioCombo.Items.AddRange(Enum.GetNames<OperatingScenario>());

        layout.Controls.Add(_autoModeCheck, 0, 0);
        layout.SetColumnSpan(_autoModeCheck, 2);
        layout.Controls.Add(new Label { Text = "Scenario", AutoSize = true }, 2, 0);
        layout.Controls.Add(_scenarioCombo, 3, 0);
        layout.Controls.Add(new Label { Text = "Pack V", AutoSize = true }, 0, 1);
        layout.Controls.Add(_packVoltageInput, 1, 1);
        layout.Controls.Add(new Label { Text = "Current A", AutoSize = true }, 2, 1);
        layout.Controls.Add(_currentInput, 3, 1);
        layout.Controls.Add(new Label { Text = "SOC %", AutoSize = true }, 4, 1);
        layout.Controls.Add(_socInput, 5, 1);
        layout.Controls.Add(new Label { Text = "Cell V", AutoSize = true }, 6, 1);
        layout.Controls.Add(_cellVoltageInput, 7, 1);
        layout.Controls.Add(new Label { Text = "Max temp C", AutoSize = true }, 0, 2);
        layout.Controls.Add(_maxTempInput, 1, 2);
        layout.Controls.Add(new Label { Text = "Min temp C", AutoSize = true }, 2, 2);
        layout.Controls.Add(_minTempInput, 3, 2);
        layout.Controls.Add(new Label { Text = "Balance mask", AutoSize = true }, 4, 2);
        layout.Controls.Add(_balanceMaskInput, 5, 2);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox CreateTimingGroup()
    {
        var group = new GroupBox { Text = "Periodic Transmission", Dock = DockStyle.Top, AutoSize = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, AutoSize = true, Padding = new Padding(10) };
        for (var i = 0; i < 8; i++)
        {
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 12.5f));
        }

        layout.Controls.Add(new Label { Text = "Cycle ms", AutoSize = true }, 0, 0);
        layout.Controls.Add(_packIntervalInput, 1, 0);
        layout.Controls.Add(new Label { Text = "Reserved", AutoSize = true }, 2, 0);
        layout.Controls.Add(_temperatureIntervalInput, 3, 0);
        layout.Controls.Add(new Label { Text = "Reserved", AutoSize = true }, 4, 0);
        layout.Controls.Add(_cellIntervalInput, 5, 0);
        layout.Controls.Add(_checksumCheck, 6, 0);
        layout.SetColumnSpan(_checksumCheck, 2);
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox CreateFileReplayGroup()
    {
        var group = new GroupBox { Text = "Simulation File Replay", Dock = DockStyle.Top, AutoSize = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 8, AutoSize = true, Padding = new Padding(10) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        layout.Controls.Add(new Label { Text = "File", AutoSize = true, Anchor = AnchorStyles.Left }, 0, 0);
        layout.Controls.Add(_filePathText, 1, 0);
        layout.Controls.Add(_browseFileButton, 2, 0);
        layout.Controls.Add(_useFileDataCheck, 3, 0);
        layout.Controls.Add(_loopFileCheck, 4, 0);
        layout.Controls.Add(new Label { Text = "Row ms", AutoSize = true, Anchor = AnchorStyles.Left }, 5, 0);
        layout.Controls.Add(_fileRowIntervalInput, 6, 0);
        layout.Controls.Add(_fileInfoLabel, 1, 1);
        layout.SetColumnSpan(_fileInfoLabel, 7);
        _filePathText.Dock = DockStyle.Fill;
        group.Controls.Add(layout);
        return group;
    }

    private GroupBox CreateLogGroup()
    {
        var group = new GroupBox { Text = "Transmission Log (last 20 messages)", Dock = DockStyle.Fill };
        group.Controls.Add(_logList);
        return group;
    }

    private Control CreateStatusPanel()
    {
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
        panel.Controls.Add(_rateLabel);
        panel.Controls.Add(new Label { Text = "   |   ", AutoSize = true });
        panel.Controls.Add(_queueLabel);
        return panel;
    }

    private void BindEvents()
    {
        _refreshButton.Click += (_, _) => RefreshPorts();
        _connectButton.Click += async (_, _) => await ToggleConnectionAsync();
        _startButton.Click += async (_, _) => await ToggleTransmissionAsync();
        _browseFileButton.Click += async (_, _) => await BrowseAndLoadSimulationFileAsync();
        _autoModeCheck.CheckedChanged += (_, _) => PushUiToSimulator();
        _scenarioCombo.SelectedIndexChanged += (_, _) => PushUiToSimulator();
        _useFileDataCheck.CheckedChanged += (_, _) =>
        {
            _snapshotProvider.UseFileSource = _useFileDataCheck.Checked;
            _config.SimulationFile.UseFileData = _useFileDataCheck.Checked;
            UpdateFileReplayUi();
        };
        _loopFileCheck.CheckedChanged += (_, _) =>
        {
            _fileDataSource.LoopFile = _loopFileCheck.Checked;
            _config.SimulationFile.LoopFile = _loopFileCheck.Checked;
        };
        _fileRowIntervalInput.ValueChanged += (_, _) =>
        {
            _fileDataSource.SetRowInterval((int)_fileRowIntervalInput.Value);
            _config.SimulationFile.ReplayRowIntervalMs = (int)_fileRowIntervalInput.Value;
        };
        _checksumCheck.CheckedChanged += (_, _) =>
        {
            _transmissionService.SetAppendChecksumToWireFormat(_checksumCheck.Checked);
            _config.Serial.AppendChecksumToWireFormat = _checksumCheck.Checked;
        };

        foreach (var input in GetManualInputs())
        {
            input.ValueChanged += (_, _) => PushUiToSimulator();
        }

        foreach (var input in GetIntervalInputs())
        {
            input.ValueChanged += (_, _) => ApplyIntervalsFromUi();
        }

        _transport.StatusChanged += (_, message) => SafeUi(() =>
        {
            _queueLabel.Text = message;
            UpdateConnectionUi();
        });
        _transmissionService.FrameTransmitted += (_, args) => SafeUi(() => AddLog(args));
        _transmissionService.Error += (_, message) => SafeUi(() => _queueLabel.Text = $"Error: {message}");
        _transmissionService.RateUpdated += (_, args) => SafeUi(() => _rateLabel.Text = $"TX rate: {args.FramesPerSecond:0} fps");
        _fileInfoTimer.Tick += (_, _) => UpdateFileReplayUi();
        _fileInfoTimer.Start();
    }

    private void LoadSettingsIntoUi()
    {
        _baudText.Text = _config.Serial.BaudRate.ToString();
        _checksumCheck.Checked = _config.Serial.AppendChecksumToWireFormat;
        _scenarioCombo.SelectedItem = _config.Simulation.InitialScenario.ToString();
        if (_scenarioCombo.SelectedIndex < 0)
        {
            _scenarioCombo.SelectedIndex = 0;
        }

        SetNumeric(_packVoltageInput, _config.Simulation.DefaultPackVoltageVolts);
        SetNumeric(_currentInput, _config.Simulation.DefaultCurrentAmps);
        SetNumeric(_socInput, _config.Simulation.DefaultSocPercent);
        SetNumeric(_maxTempInput, _config.Simulation.DefaultMaxTemperatureC);
        SetNumeric(_minTempInput, _config.Simulation.DefaultMinTemperatureC);
        SetNumeric(_cellVoltageInput, _config.Simulation.DefaultCellVoltageVolts);
        SetNumeric(_packIntervalInput, _config.Intervals.PackStatusMs);
        SetNumeric(_temperatureIntervalInput, _config.Intervals.TemperatureMs);
        SetNumeric(_cellIntervalInput, _config.Intervals.CellVoltageMs);
        _filePathText.Text = _config.SimulationFile.LastFilePath;
        _useFileDataCheck.Checked = _config.SimulationFile.UseFileData;
        _loopFileCheck.Checked = _config.SimulationFile.LoopFile;
        SetNumeric(_fileRowIntervalInput, Math.Max(1000, _config.SimulationFile.ReplayRowIntervalMs));
        _fileDataSource.LoopFile = _loopFileCheck.Checked;
        _fileDataSource.SetRowInterval((int)_fileRowIntervalInput.Value);
        _snapshotProvider.UseFileSource = _useFileDataCheck.Checked;
    }

    private void RefreshPorts()
    {
        var selected = GetSelectedPortName();
        _portCombo.Items.Clear();
        foreach (var port in SerialPortDiscovery.GetAvailablePortInfos())
        {
            _portCombo.Items.Add(port);
        }

        var existing = _portCombo.Items
            .OfType<SerialPortInfo>()
            .FirstOrDefault(port => port.PortName.Equals(_config.Serial.PortName, StringComparison.OrdinalIgnoreCase));

        if (existing is null && !string.IsNullOrWhiteSpace(_config.Serial.PortName))
        {
            existing = new SerialPortInfo(_config.Serial.PortName, "not detected");
            _portCombo.Items.Add(existing);
        }

        var target = !string.IsNullOrWhiteSpace(selected) ? selected : _config.Serial.PortName;
        var targetInfo = _portCombo.Items
            .OfType<SerialPortInfo>()
            .FirstOrDefault(port => port.PortName.Equals(target, StringComparison.OrdinalIgnoreCase));

        if (targetInfo is not null)
        {
            _portCombo.SelectedItem = targetInfo;
            _selectedPortInfo = targetInfo;
        }
        else
        {
            _portCombo.Text = target;
            _selectedPortInfo = null;
        }
    }

    private async Task ToggleConnectionAsync()
    {
        if (_transport.IsOpen)
        {
            if (_transmissionService.IsRunning)
            {
                await _transmissionService.StopAsync();
            }

            _transport.Close();
            UpdateConnectionUi();
            return;
        }

        if (!int.TryParse(_baudText.Text, out var baudRate) || baudRate <= 0)
        {
            MessageBox.Show(this, "Baud rate tidak valid.", "Serial Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var portName = GetSelectedPortName();
        if (string.IsNullOrWhiteSpace(portName))
        {
            MessageBox.Show(this, "Pilih COM port terlebih dahulu.", "Serial Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            _selectedPortInfo = GetSelectedPortInfo();
            if (_selectedPortInfo?.IsBluetooth == true)
            {
                var result = MessageBox.Show(
                    this,
                    $"{_selectedPortInfo.PortName} terdeteksi sebagai Bluetooth serial endpoint ({_selectedPortInfo.DeviceName}), bukan com0com. Data hanya akan terbaca aplikasi lain jika port ini benar-benar punya pasangan virtual. Lanjut connect?",
                    "Port Warning",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning);
                if (result != DialogResult.Yes)
                {
                    return;
                }
            }

            _config.Serial.PortName = portName;
            _config.Serial.BaudRate = baudRate;
            await _transport.OpenAsync(new SerialOptions(portName, baudRate), CancellationToken.None);
            UpdateConnectionUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Serial Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _queueLabel.Text = $"Connect failed: {ex.Message}";
            UpdateConnectionUi();
        }
    }

    private async Task ToggleTransmissionAsync()
    {
        if (_transmissionService.IsRunning)
        {
            await _transmissionService.StopAsync();
            _startButton.Text = "Start TX";
            _queueLabel.Text = "Transmission stopped.";
            return;
        }

        if (!_transport.IsOpen)
        {
            await ToggleConnectionAsync();
            if (!_transport.IsOpen)
            {
                return;
            }
        }

        if (_useFileDataCheck.Checked && !_fileDataSource.HasRows)
        {
            MessageBox.Show(this, "Pilih dan load file simulasi terlebih dahulu.", "Simulation File", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        ApplyIntervalsFromUi();
        PushUiToSimulator();
        _transmissionService.Start();
        _startButton.Text = "Stop TX";
        _queueLabel.Text = _snapshotProvider.UseFileSource && _fileDataSource.HasRows
            ? "Transmission running from file data."
            : "Transmission running from generated data.";
    }

    private async Task BrowseAndLoadSimulationFileAsync()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "Select BMS simulation file",
            Filter = "Simulation files (*.csv;*.tsv;*.xlsx;*.xlsm)|*.csv;*.tsv;*.xlsx;*.xlsm|CSV files (*.csv)|*.csv|Excel files (*.xlsx;*.xlsm)|*.xlsx;*.xlsm|All files (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (!string.IsNullOrWhiteSpace(_filePathText.Text))
        {
            dialog.FileName = _filePathText.Text;
        }

        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        await LoadSimulationFileAsync(dialog.FileName);
    }

    private async Task LoadSimulationFileAsync(string filePath)
    {
        try
        {
            _fileInfoLabel.Text = "Loading simulation file...";
            var data = await Task.Run(() => SimulationFileReader.Load(filePath, _config.Simulation));
            _fileDataSource.Load(data);
            _filePathText.Text = filePath;
            _config.SimulationFile.LastFilePath = filePath;
            _useFileDataCheck.Checked = true;
            _snapshotProvider.UseFileSource = true;

            var warningText = data.Warnings.Count == 0 ? string.Empty : $" Warning: {string.Join(" ", data.Warnings)}";
            _queueLabel.Text = $"Loaded {data.Rows.Count} simulation rows.{warningText}";
            UpdateFileReplayUi();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Simulation File Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _queueLabel.Text = $"File load failed: {ex.Message}";
        }
    }

    private void PushUiToSimulator()
    {
        if (_scenarioCombo.SelectedItem is null)
        {
            return;
        }

        var scenario = Enum.Parse<OperatingScenario>(_scenarioCombo.SelectedItem.ToString()!);
        _simulator.SetAutoMode(_autoModeCheck.Checked);
        _simulator.SetScenario(scenario);
        _simulator.SetManualInput(new ManualBmsInput(
            (double)_packVoltageInput.Value,
            (double)_currentInput.Value,
            (byte)_socInput.Value,
            (byte)_maxTempInput.Value,
            (byte)_minTempInput.Value,
            (ushort)_balanceMaskInput.Value,
            scenario,
            (double)_cellVoltageInput.Value));
    }

    private void ApplyIntervalsFromUi()
    {
        _config.Intervals.PackStatusMs = (int)_packIntervalInput.Value;
        _config.Intervals.TemperatureMs = (int)_packIntervalInput.Value;
        _config.Intervals.CellVoltageMs = (int)_packIntervalInput.Value;
        _temperatureIntervalInput.Value = _packIntervalInput.Value;
        _cellIntervalInput.Value = _packIntervalInput.Value;
        _fileRowIntervalInput.Value = _packIntervalInput.Value;
        _fileDataSource.SetRowInterval((int)_packIntervalInput.Value);
        _config.SimulationFile.ReplayRowIntervalMs = (int)_packIntervalInput.Value;
        _transmissionService.UpdateIntervals(_config.Intervals);
    }

    private void AddLog(FrameTransmittedEventArgs args)
    {
        var line = $"{args.Timestamp:HH:mm:ss.fff} TX {args.WireLine} CHK:0x{args.Checksum:X2} | {args.DecodedText}";
        _logList.Items.Insert(0, line);
        while (_logList.Items.Count > 20)
        {
            _logList.Items.RemoveAt(_logList.Items.Count - 1);
        }
    }

    private void UpdateConnectionUi()
    {
        _connectionLabel.Text = _transport.IsOpen ? "Connected" : "Disconnected";
        _connectButton.Text = _transport.IsOpen ? "Disconnect" : "Connect";
        _startButton.Enabled = _transport.IsOpen || !_transmissionService.IsRunning;
        if (!_transmissionService.IsRunning)
        {
            _startButton.Text = "Start TX";
        }
    }

    private void UpdateFileReplayUi()
    {
        var fileActive = _useFileDataCheck.Checked && _fileDataSource.HasRows;
        if (_fileDataSource.HasRows)
        {
            _fileInfoLabel.Text = $"Loaded {_fileDataSource.RowCount} rows, replay row {_fileDataSource.CurrentRowNumber}/{_fileDataSource.RowCount}.";
        }
        else
        {
            _fileInfoLabel.Text = "No simulation file loaded.";
        }

        _autoModeCheck.Enabled = !fileActive;
        _scenarioCombo.Enabled = !fileActive;
    }

    private void SaveSettingsFromUi()
    {
        _config.Serial.PortName = GetSelectedPortName();
        if (int.TryParse(_baudText.Text, out var baudRate) && baudRate > 0)
        {
            _config.Serial.BaudRate = baudRate;
        }

        _config.Serial.AppendChecksumToWireFormat = _checksumCheck.Checked;
        _config.SimulationFile.LastFilePath = _filePathText.Text;
        _config.SimulationFile.UseFileData = _useFileDataCheck.Checked;
        _config.SimulationFile.LoopFile = _loopFileCheck.Checked;
        _config.SimulationFile.ReplayRowIntervalMs = (int)_fileRowIntervalInput.Value;
        _config.Simulation.InitialScenario = Enum.Parse<OperatingScenario>(_scenarioCombo.SelectedItem?.ToString() ?? OperatingScenario.Idle.ToString());
        _config.Simulation.DefaultPackVoltageVolts = (double)_packVoltageInput.Value;
        _config.Simulation.DefaultCurrentAmps = (double)_currentInput.Value;
        _config.Simulation.DefaultSocPercent = (byte)_socInput.Value;
        _config.Simulation.DefaultMaxTemperatureC = (byte)_maxTempInput.Value;
        _config.Simulation.DefaultMinTemperatureC = (byte)_minTempInput.Value;
        _config.Simulation.DefaultCellVoltageVolts = (double)_cellVoltageInput.Value;
        ApplyIntervalsFromUi();
        var error = _configService.Save(_config);
        if (error is not null)
        {
            _queueLabel.Text = $"Config save skipped: {error}";
        }
    }

    private void SafeUi(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        if (InvokeRequired)
        {
            BeginInvoke(action);
            return;
        }

        action();
    }

    private IEnumerable<NumericUpDown> GetManualInputs()
    {
        yield return _packVoltageInput;
        yield return _currentInput;
        yield return _socInput;
        yield return _maxTempInput;
        yield return _minTempInput;
        yield return _cellVoltageInput;
        yield return _balanceMaskInput;
    }

    private IEnumerable<NumericUpDown> GetIntervalInputs()
    {
        yield return _packIntervalInput;
        yield return _temperatureIntervalInput;
        yield return _cellIntervalInput;
    }

    /// <inheritdoc />
    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        _fileInfoTimer.Stop();
        SaveSettingsFromUi();
        _transmissionService.StopAsync().GetAwaiter().GetResult();
        _transport.Close();
        _transmissionService.Dispose();
        base.OnFormClosing(e);
    }

    /// <inheritdoc />
    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);
        if (_useFileDataCheck.Checked
            && !string.IsNullOrWhiteSpace(_filePathText.Text)
            && File.Exists(ResolveSimulationFilePath(_filePathText.Text)))
        {
            await LoadSimulationFileAsync(ResolveSimulationFilePath(_filePathText.Text));
        }
    }

    private static NumericUpDown CreateNumeric(decimal minimum, decimal maximum, int decimalPlaces, decimal increment)
    {
        return new NumericUpDown
        {
            Minimum = minimum,
            Maximum = maximum,
            DecimalPlaces = decimalPlaces,
            Increment = increment,
            Width = 90
        };
    }

    private static void SetNumeric(NumericUpDown input, double value)
    {
        var decimalValue = (decimal)value;
        input.Value = Math.Min(Math.Max(decimalValue, input.Minimum), input.Maximum);
    }

    private static string ResolveSimulationFilePath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
        {
            return filePath;
        }

        return Path.Combine(AppContext.BaseDirectory, filePath);
    }

    private string GetSelectedPortName()
    {
        return GetSelectedPortInfo()?.PortName ?? _portCombo.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() ?? string.Empty;
    }

    private SerialPortInfo? GetSelectedPortInfo()
    {
        return _portCombo.SelectedItem as SerialPortInfo
            ?? _portCombo.Items
                .OfType<SerialPortInfo>()
                .FirstOrDefault(port => port.PortName.Equals(_portCombo.Text.Trim(), StringComparison.OrdinalIgnoreCase));
    }
}
