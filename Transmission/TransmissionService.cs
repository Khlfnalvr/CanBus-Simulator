using System.Collections.Concurrent;
using CanBusSimulator.Can;
using CanBusSimulator.Models;
using CanBusSimulator.Serial;
using CanBusSimulator.Simulation;

namespace CanBusSimulator.Transmission;

/// <summary>
/// Schedules BMS CAN messages, queues them, and writes them to the serial transport.
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
    private bool _appendChecksumToWireFormat;
    private int _sentSinceLastRate;

    /// <summary>
    /// Creates a transmission service using the supplied transport and simulator.
    /// </summary>
    public TransmissionService(
        ISerialTransport transport,
        IBmsSnapshotSource snapshotSource,
        SimulationSettings simulationSettings,
        TransmissionIntervals intervals,
        bool appendChecksumToWireFormat)
    {
        _transport = transport;
        _snapshotSource = snapshotSource;
        _simulationSettings = simulationSettings;
        UpdateIntervals(intervals);
        _appendChecksumToWireFormat = appendChecksumToWireFormat;
    }

    /// <summary>
    /// Raised after each frame is written successfully.
    /// </summary>
    public event EventHandler<FrameTransmittedEventArgs>? FrameTransmitted;

    /// <summary>
    /// Raised when a recoverable transmission or reconnect error occurs.
    /// </summary>
    public event EventHandler<string>? Error;

    /// <summary>
    /// Raised every second with measured frames-per-second.
    /// </summary>
    public event EventHandler<TransmissionRateEventArgs>? RateUpdated;

    /// <summary>
    /// True while the background worker is active.
    /// </summary>
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

    /// <summary>
    /// Starts periodic frame generation and transmission.
    /// </summary>
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

    /// <summary>
    /// Stops the background worker and waits for it to finish.
    /// </summary>
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

    /// <summary>
    /// Updates message periods without restarting transmission.
    /// </summary>
    public void UpdateIntervals(TransmissionIntervals intervals)
    {
        lock (_settingsGate)
        {
            _packInterval = TimeSpan.FromMilliseconds(Math.Max(1000, intervals.PackStatusMs));
            _temperatureInterval = TimeSpan.FromMilliseconds(Math.Max(1000, intervals.TemperatureMs));
            _cellInterval = TimeSpan.FromMilliseconds(Math.Max(1000, intervals.CellVoltageMs));
        }
    }

    /// <summary>
    /// Enables or disables appending the optional checksum field to the serial line.
    /// </summary>
    public void SetAppendChecksumToWireFormat(bool enabled)
    {
        lock (_settingsGate)
        {
            _appendChecksumToWireFormat = enabled;
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
        var nextRate = lastUpdate.AddSeconds(1);
        var nextReconnectAttempt = DateTimeOffset.MinValue;
        BmsSnapshot snapshot;

        while (!cancellationToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - lastUpdate;
            lastUpdate = now;
            snapshot = _snapshotSource.Update(elapsed);

            TimeSpan packInterval;
            lock (_settingsGate)
            {
                packInterval = _packInterval;
            }

            if (now >= nextPack)
            {
                foreach (var frame in BmsCanFrameFactory.CreateFullCycle(snapshot, _simulationSettings))
                {
                    _queue.Enqueue(frame);
                }

                nextPack = now + packInterval;
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

            await Task.Delay(20, cancellationToken).ConfigureAwait(false);
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
            lock (_settingsGate)
            {
                includeChecksum = _appendChecksumToWireFormat;
            }

            try
            {
                var wireLine = frame.ToWireString(includeChecksum);
                await _transport.WriteLineAsync(wireLine, cancellationToken).ConfigureAwait(false);
                Interlocked.Increment(ref _sentSinceLastRate);
                FrameTransmitted?.Invoke(
                    this,
                    new FrameTransmittedEventArgs(
                        frame,
                        wireLine.TrimEnd('\r', '\n'),
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
