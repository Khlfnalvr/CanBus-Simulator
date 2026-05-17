using System.Collections.Concurrent;
using CanBusSimulator.Can;
using CanBusSimulator.Models;
using CanBusSimulator.Serial;
using CanBusSimulator.Simulation;

namespace CanBusSimulator.Transmission;

/// <summary>
/// Schedules BMS CAN messages with realistic per-group cadence (mimicking an ESP master
/// pushing CAN frames out through UART) and writes them to the serial transport.
/// </summary>
public sealed class TransmissionService : IDisposable
{
    private readonly object _stateGate = new();
    private readonly object _settingsGate = new();
    private readonly ConcurrentQueue<CanFrame> _queue = new();
    private readonly ISerialTransport _transport;
    private readonly IBmsSnapshotSource _snapshotSource;
    private readonly SimulationSettings _simulationSettings;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerTask;
    private TimeSpan _packInterval;
    private TimeSpan _temperatureInterval;
    private TimeSpan _cellInterval;
    private TimeSpan _balancingInterval;
    private TimeSpan _diagnosticInterval;
    private TimeSpan _heartbeatInterval;
    private bool _appendChecksumToWireFormat;
    private WireFormat _wireFormat = WireFormat.Custom;
    private int _sentSinceLastRate;

    /// <summary>
    /// Creates a transmission service using the supplied transport and simulator.
    /// </summary>
    public TransmissionService(
        ISerialTransport transport,
        IBmsSnapshotSource snapshotSource,
        SimulationSettings simulationSettings,
        TransmissionIntervals intervals,
        bool appendChecksumToWireFormat,
        WireFormat wireFormat)
    {
        _transport = transport;
        _snapshotSource = snapshotSource;
        _simulationSettings = simulationSettings;
        UpdateIntervals(intervals);
        _appendChecksumToWireFormat = appendChecksumToWireFormat;
        _wireFormat = wireFormat;
    }

    /// <summary>Raised after each frame is written successfully.</summary>
    public event EventHandler<FrameTransmittedEventArgs>? FrameTransmitted;

    /// <summary>Raised when a recoverable transmission or reconnect error occurs.</summary>
    public event EventHandler<string>? Error;

    /// <summary>Raised every second with measured frames-per-second.</summary>
    public event EventHandler<TransmissionRateEventArgs>? RateUpdated;

    /// <summary>True while the background worker is active.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
            {
                return _workerTask is { IsCompleted: false };
            }
        }
    }

    /// <summary>Starts periodic frame generation and transmission.</summary>
    public void Start()
    {
        lock (_stateGate)
        {
            if (_workerTask is { IsCompleted: false })
            {
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            _workerTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token));
        }
    }

    /// <summary>Stops the background worker and waits for it to finish.</summary>
    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_stateGate)
        {
            cts = _cancellationTokenSource;
            task = _workerTask;
        }

        if (cts is null || task is null)
        {
            return;
        }

        cts.Cancel();
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
            lock (_stateGate)
            {
                if (ReferenceEquals(_cancellationTokenSource, cts))
                {
                    _cancellationTokenSource = null;
                    _workerTask = null;
                }
            }
        }
    }

    /// <summary>Updates message periods without restarting transmission.</summary>
    public void UpdateIntervals(TransmissionIntervals intervals)
    {
        lock (_settingsGate)
        {
            _packInterval = TimeSpan.FromMilliseconds(Math.Max(50, intervals.PackStatusMs));
            _temperatureInterval = TimeSpan.FromMilliseconds(Math.Max(50, intervals.TemperatureMs));
            _cellInterval = TimeSpan.FromMilliseconds(Math.Max(50, intervals.CellVoltageMs));
            _balancingInterval = TimeSpan.FromMilliseconds(Math.Max(50, intervals.BalancingMs));
            _diagnosticInterval = TimeSpan.FromMilliseconds(Math.Max(50, intervals.DiagnosticMs));
            _heartbeatInterval = TimeSpan.FromMilliseconds(Math.Max(50, intervals.HeartbeatMs));
        }
    }

    /// <summary>Enables or disables appending an XOR checksum field to the wire output.</summary>
    public void SetAppendChecksumToWireFormat(bool enabled)
    {
        lock (_settingsGate)
        {
            _appendChecksumToWireFormat = enabled;
        }
    }

    /// <summary>Switches between Custom text, SLCAN, and Binary wire formats at runtime.</summary>
    public void SetWireFormat(WireFormat format)
    {
        lock (_settingsGate)
        {
            _wireFormat = format;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var lastUpdate = DateTimeOffset.UtcNow;
        var nextPack = lastUpdate;
        var nextTemperature = lastUpdate;
        var nextCell = lastUpdate;
        var nextBalancing = lastUpdate;
        var nextDiagnostic = lastUpdate;
        var nextHeartbeat = lastUpdate;
        var nextRate = lastUpdate.AddSeconds(1);
        var nextReconnectAttempt = DateTimeOffset.MinValue;
        byte heartbeatCounter = 0;

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - lastUpdate;
            lastUpdate = now;
            var snapshot = _snapshotSource.Update(elapsed);

            TimeSpan packInt, tempInt, cellInt, balInt, diagInt, hbInt;
            lock (_settingsGate)
            {
                packInt = _packInterval;
                tempInt = _temperatureInterval;
                cellInt = _cellInterval;
                balInt = _balancingInterval;
                diagInt = _diagnosticInterval;
                hbInt = _heartbeatInterval;
            }

            if (now >= nextPack)
            {
                _queue.Enqueue(BmsCanFrameFactory.CreatePackStatus(snapshot, _simulationSettings));
                nextPack = now + packInt;
            }

            if (now >= nextCell)
            {
                for (var group = 0; group < 5; group++)
                {
                    _queue.Enqueue(BmsCanFrameFactory.CreateCellVoltageGroup(snapshot, group));
                }
                nextCell = now + cellInt;
            }

            if (now >= nextTemperature)
            {
                for (var group = 0; group < 3; group++)
                {
                    _queue.Enqueue(BmsCanFrameFactory.CreateTemperatureGroup(snapshot, group));
                }
                nextTemperature = now + tempInt;
            }

            if (now >= nextBalancing)
            {
                _queue.Enqueue(BmsCanFrameFactory.CreateBalancing(snapshot));
                nextBalancing = now + balInt;
            }

            if (now >= nextDiagnostic)
            {
                _queue.Enqueue(BmsCanFrameFactory.CreateDiagnostic(snapshot));
                nextDiagnostic = now + diagInt;
            }

            if (now >= nextHeartbeat)
            {
                _queue.Enqueue(BmsCanFrameFactory.CreateHeartbeat(snapshot, heartbeatCounter++));
                nextHeartbeat = now + hbInt;
            }

            if (!_transport.IsOpen && now >= nextReconnectAttempt)
            {
                await TryReconnectAsync(cancellationToken).ConfigureAwait(false);
                nextReconnectAttempt = now.AddSeconds(1);
            }

            await DrainQueueAsync(cancellationToken).ConfigureAwait(false);

            if (now >= nextRate)
            {
                var sent = Interlocked.Exchange(ref _sentSinceLastRate, 0);
                RateUpdated?.Invoke(this, new TransmissionRateEventArgs(sent));
                nextRate = now.AddSeconds(1);
            }

            await Task.Delay(10, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task TryReconnectAsync(CancellationToken cancellationToken)
    {
        if (_transport.CurrentOptions is null)
        {
            return;
        }

        if (!await _transport.TryReconnectAsync(cancellationToken).ConfigureAwait(false))
        {
            Error?.Invoke(this, $"Reconnect failed for {_transport.CurrentOptions.PortName}. Will retry.");
        }
    }

    private async Task DrainQueueAsync(CancellationToken cancellationToken)
    {
        while (_queue.TryDequeue(out var frame))
        {
            if (!_transport.IsOpen)
            {
                _queue.Enqueue(frame);
                return;
            }

            bool includeChecksum;
            WireFormat format;
            lock (_settingsGate)
            {
                includeChecksum = _appendChecksumToWireFormat;
                format = _wireFormat;
            }

            try
            {
                var bytes = frame.Render(format, includeChecksum);
                await _transport.WriteBytesAsync(bytes, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _sentSinceLastRate);
                FrameTransmitted?.Invoke(
                    this,
                    new FrameTransmittedEventArgs(
                        frame,
                        frame.ToDisplayString(format, includeChecksum),
                        BmsCanFrameFactory.Describe(frame, _simulationSettings),
                        DateTimeOffset.Now));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
            {
                _queue.Enqueue(frame);
                Error?.Invoke(this, ex.Message);
                return;
            }
        }
    }
}
