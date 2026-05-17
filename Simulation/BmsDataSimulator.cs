using CanBusSimulator.Models;

namespace CanBusSimulator.Simulation;

/// <summary>
/// Generates realistic BMS values for a 20S pack with optional manual overrides.
/// Models pack current, SOC integration, cell voltages, temperatures, balancing
/// activity, and cycle accumulation in a way an ESP master would see them.
/// </summary>
public sealed class BmsDataSimulator : IBmsSnapshotSource
{
    private readonly object _syncRoot = new();
    private readonly Random _random = new();
    private readonly SimulationSettings _settings;
    private readonly double[] _cellOffsets = new double[20];
    private readonly double[] _temperatureOffsets = new double[10];
    private bool _autoMode = true;
    private double _packVoltageVolts;
    private double _packCurrentAmps;
    private double _socPercent;
    private double _maxTemperatureC;
    private double _minTemperatureC;
    private ushort _activeBalanceCells;
    private OperatingScenario _scenario;
    private ManualBmsInput _manualInput;
    private int _cycleCount;
    private double _accumulatedChargeAh;

    /// <summary>
    /// Creates a simulator with values seeded from configuration.
    /// </summary>
    public BmsDataSimulator(SimulationSettings settings)
    {
        _settings = settings;
        _scenario = settings.InitialScenario;
        _packVoltageVolts = Clamp(settings.DefaultPackVoltageVolts, 60.0, 84.0);
        _packCurrentAmps = Clamp(settings.DefaultCurrentAmps, -150.0, 150.0);
        _socPercent = Clamp(settings.DefaultSocPercent, 0, 100);
        _maxTemperatureC = Clamp(settings.DefaultMaxTemperatureC, 0, 80);
        _minTemperatureC = Clamp(settings.DefaultMinTemperatureC, 0, 80);
        _manualInput = new ManualBmsInput(
            _packVoltageVolts,
            _packCurrentAmps,
            (byte)Math.Round(_socPercent),
            (byte)Math.Round(_maxTemperatureC),
            (byte)Math.Round(_minTemperatureC),
            0,
            _scenario,
            Clamp(settings.DefaultCellVoltageVolts, 3.0, 4.2));

        for (var index = 0; index < _cellOffsets.Length; index++)
        {
            _cellOffsets[index] = (_random.NextDouble() - 0.5) * 0.018;
        }

        for (var index = 0; index < _temperatureOffsets.Length; index++)
        {
            _temperatureOffsets[index] = (_random.NextDouble() - 0.5) * 2.0;
        }
    }

    /// <summary>Switches between automatic simulation and user-provided values.</summary>
    public void SetAutoMode(bool enabled)
    {
        lock (_syncRoot)
        {
            _autoMode = enabled;
        }
    }

    /// <summary>Sets the scenario used by automatic simulation and the status bytes.</summary>
    public void SetScenario(OperatingScenario scenario)
    {
        lock (_syncRoot)
        {
            _scenario = scenario;
        }
    }

    /// <summary>Stores the manual values that will be emitted when auto mode is disabled.</summary>
    public void SetManualInput(ManualBmsInput manualInput)
    {
        lock (_syncRoot)
        {
            _manualInput = manualInput;
        }
    }

    /// <summary>Advances the simulation and returns the latest BMS snapshot.</summary>
    public BmsSnapshot Update(TimeSpan elapsed)
    {
        lock (_syncRoot)
        {
            if (!_autoMode)
            {
                return CreateManualSnapshotLocked();
            }

            UpdateAutomaticStateLocked(Math.Max(0.0, elapsed.TotalSeconds));
            return CreateAutomaticSnapshotLocked();
        }
    }

    private void UpdateAutomaticStateLocked(double seconds)
    {
        var wave = 0.5 + (Math.Sin(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 5000.0) * 0.5);
        var noise = (_random.NextDouble() - 0.5) * 1.8;
        var targetCurrent = _scenario switch
        {
            OperatingScenario.Charging => 7.0 + (wave * 23.0) + noise,
            OperatingScenario.Discharging => -8.0 - (wave * 35.0) + noise,
            _ => (_random.NextDouble() - 0.5) * 1.2
        };

        _packCurrentAmps = Smooth(_packCurrentAmps, Clamp(targetCurrent, -150.0, 150.0), seconds, 1.4);

        var capacityAh = Math.Max(1.0, _settings.NominalCapacityAh);
        var deltaSoc = _packCurrentAmps * seconds / 3600.0 / capacityAh * 100.0;
        _socPercent = Clamp(_socPercent + deltaSoc, 0.0, 100.0);

        // accumulate charge-throughput to estimate cycle count
        _accumulatedChargeAh += Math.Abs(_packCurrentAmps) * seconds / 3600.0;
        if (_accumulatedChargeAh >= capacityAh * 2.0)
        {
            _accumulatedChargeAh -= capacityAh * 2.0;
            _cycleCount++;
        }

        var openCircuitVoltage = 60.0 + (_socPercent / 100.0 * 24.0);
        var currentSag = _packCurrentAmps < 0
            ? -Math.Min(2.5, Math.Abs(_packCurrentAmps) * 0.02)
            : Math.Min(1.5, _packCurrentAmps * 0.018);
        _packVoltageVolts = Smooth(_packVoltageVolts, Clamp(openCircuitVoltage + currentSag, 60.0, 84.0), seconds, 0.8);

        var heatFromCurrent = Math.Abs(_packCurrentAmps) * 0.45;
        var targetMaxTemp = Clamp(28.0 + heatFromCurrent + (_scenario == OperatingScenario.Charging ? 2.0 : 0.0), 20.0, 70.0);
        _maxTemperatureC = Smooth(_maxTemperatureC, targetMaxTemp + ((_random.NextDouble() - 0.5) * 0.4), seconds, 0.7);
        _minTemperatureC = Smooth(_minTemperatureC, Clamp(_maxTemperatureC - 5.0 - (_random.NextDouble() * 4.0), 20.0, _maxTemperatureC), seconds, 0.7);

        _activeBalanceCells = BuildBalanceMask();
    }

    private BmsSnapshot CreateAutomaticSnapshotLocked()
    {
        var cells = BuildCellVoltages(_packVoltageVolts / 20.0);
        var temps = BuildTemperatures(_minTemperatureC, _maxTemperatureC);
        var balanceFlags = BuildBalanceFlags(_activeBalanceCells);
        return new BmsSnapshot(
            Math.Round(_packVoltageVolts, 3),
            Math.Round(_packCurrentAmps, 3),
            (byte)Math.Round(Clamp(_socPercent, 0.0, 100.0)),
            (byte)Math.Round(Clamp(_maxTemperatureC, 0.0, 80.0)),
            (byte)Math.Round(Clamp(_minTemperatureC, 0.0, 80.0)),
            _activeBalanceCells,
            _scenario,
            cells,
            temps,
            balanceFlags,
            DateTimeOffset.Now,
            _cycleCount);
    }

    private BmsSnapshot CreateManualSnapshotLocked()
    {
        var cellVoltage = Clamp(_manualInput.CellVoltageVolts, 3.0, 4.2);
        var cells = Enumerable.Repeat(cellVoltage, 20).ToArray();
        var temps = BuildManualTemperatures(_manualInput.MinTemperatureC, _manualInput.MaxTemperatureC);
        return new BmsSnapshot(
            Clamp(_manualInput.PackVoltageVolts, 60.0, 84.0),
            Clamp(_manualInput.PackCurrentAmps, -150.0, 150.0),
            (byte)Clamp(_manualInput.SocPercent, 0, 100),
            (byte)Clamp(_manualInput.MaxTemperatureC, 0, 80),
            (byte)Clamp(_manualInput.MinTemperatureC, 0, 80),
            _manualInput.ActiveBalanceCells,
            _manualInput.Scenario,
            cells,
            temps,
            BuildBalanceFlags(_manualInput.ActiveBalanceCells),
            DateTimeOffset.Now,
            _cycleCount);
    }

    private double[] BuildCellVoltages(double averageCellVoltage)
    {
        var cells = new double[20];
        for (var index = 0; index < cells.Length; index++)
        {
            var jitter = (_random.NextDouble() - 0.5) * 0.004;
            cells[index] = Math.Round(Clamp(averageCellVoltage + _cellOffsets[index] + jitter, 3.0, 4.2), 4);
        }

        return cells;
    }

    private double[] BuildTemperatures(double minTemp, double maxTemp)
    {
        var temps = new double[10];
        for (var index = 0; index < temps.Length; index++)
        {
            var position = temps.Length == 1 ? 0.0 : index / (double)(temps.Length - 1);
            var wave = Math.Sin((DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 3000.0) + index) * 0.35;
            temps[index] = Math.Round(Clamp(minTemp + ((maxTemp - minTemp) * position) + _temperatureOffsets[index] + wave, 0.0, 120.0), 2);
        }

        return temps;
    }

    private static double[] BuildManualTemperatures(double minTemp, double maxTemp)
    {
        var temps = new double[10];
        for (var index = 0; index < temps.Length; index++)
        {
            var position = temps.Length == 1 ? 0.0 : index / (double)(temps.Length - 1);
            temps[index] = Math.Round(minTemp + ((maxTemp - minTemp) * position), 2);
        }

        return temps;
    }

    private static bool[] BuildBalanceFlags(ushort mask)
    {
        var flags = new bool[20];
        for (var index = 0; index < flags.Length; index++)
        {
            flags[index] = (mask & (1 << index)) != 0;
        }

        return flags;
    }

    private ushort BuildBalanceMask()
    {
        if (_scenario != OperatingScenario.Charging || _socPercent < 80.0)
        {
            return 0;
        }

        var mask = 0;
        for (var cell = 0; cell < 16; cell++)
        {
            if (_random.NextDouble() > 0.82)
            {
                mask |= 1 << cell;
            }
        }

        return (ushort)mask;
    }

    private static double Smooth(double current, double target, double seconds, double responsePerSecond)
    {
        var alpha = Clamp(seconds * responsePerSecond, 0.0, 1.0);
        return current + ((target - current) * alpha);
    }

    private static double Clamp(double value, double min, double max)
    {
        return Math.Min(Math.Max(value, min), max);
    }

    private static int Clamp(int value, int min, int max)
    {
        return Math.Min(Math.Max(value, min), max);
    }
}
