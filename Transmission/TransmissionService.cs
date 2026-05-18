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
    private readonly ISerialTransport _transport;
    private readonly IBmsSnapshotSource _snapshotSource;
    private readonly SimulationSettings _simulationSettings;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerTask;

    // Snapshot of mutable settings: written atomically on every change, read once per loop.
    private volatile RuntimeSettings _settings;

    // Stats (Interlocked-updated, snapshot read from any thread).
    private long _totalFrames;
    private long _totalBytes;
    private long _totalErrors;
    private DateTimeOffset _startedAt;
    private int _sentSinceLastRate;

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
        _settings = RuntimeSettings.From(intervals, appendChecksumToWireFormat, wireFormat);
    }

    public event EventHandler<FrameTransmittedEventArgs>? FrameTransmitted;
    public event EventHandler<string>? Error;
    public event EventHandler<TransmissionRateEventArgs>? RateUpdated;

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

    public long TotalFrames => Interlocked.Read(ref _totalFrames);
    public long TotalBytes => Interlocked.Read(ref _totalBytes);
    public long TotalErrors => Interlocked.Read(ref _totalErrors);
    public DateTimeOffset? StartedAt => _startedAt == default ? null : _startedAt;

    public void Start()
    {
        lock (_stateGate)
        {
            if (_workerTask is { IsCompleted: false }) return;

            _cancellationTokenSource = new CancellationTokenSource();
            _startedAt = DateTimeOffset.UtcNow;
            _workerTask = Task.Run(() => RunAsync(_cancellationTokenSource.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_stateGate)
        {
            cts = _cancellationTokenSource;
            task = _workerTask;
        }

        if (cts is null || task is null) return;

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

    public void UpdateIntervals(TransmissionIntervals intervals)
    {
        var s = _settings;
        _settings = s.WithIntervals(intervals);
    }

    public void SetAppendChecksumToWireFormat(bool enabled)
    {
        var s = _settings;
        _settings = s with { AppendChecksum = enabled };
    }

    public void SetWireFormat(WireFormat format)
    {
        var s = _settings;
        _settings = s with { Format = format };
    }

    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _totalFrames, 0);
        Interlocked.Exchange(ref _totalBytes, 0);
        Interlocked.Exchange(ref _totalErrors, 0);
        _startedAt = IsRunning ? DateTimeOffset.UtcNow : default;
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private async Task RunAsync(CancellationToken ct)
    {
        // Reused write buffer — sized to fit the largest possible wire-format encoding.
        var writeBuffer = new byte[64];

        var lastUpdate = DateTimeOffset.UtcNow;
        var nextPack = lastUpdate;
        var nextTemperature = lastUpdate;
        var nextCell = lastUpdate;
        var nextBalancing = lastUpdate;
        var nextDiagnostic = lastUpdate;
        var nextHeartbeat = lastUpdate;
        var nextRate = lastUpdate.AddSeconds(1);
        var nextReconnect = DateTimeOffset.MinValue;
        byte heartbeatCounter = 0;

        using var periodic = new PeriodicTimer(TimeSpan.FromMilliseconds(10));

        while (!ct.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var elapsed = now - lastUpdate;
            lastUpdate = now;
            var snapshot = _snapshotSource.Update(elapsed);
            var s = _settings; // single volatile read per iteration

            if (!_transport.IsOpen && now >= nextReconnect)
            {
                await TryReconnectAsync(ct).ConfigureAwait(false);
                nextReconnect = now.AddSeconds(1);
            }

            if (now >= nextPack)
            {
                await SendAsync(BmsCanFrameFactory.CreatePackStatus(snapshot, _simulationSettings), writeBuffer, s, ct).ConfigureAwait(false);
                nextPack = now + s.PackInterval;
            }

            if (now >= nextCell)
            {
                for (var g = 0; g < 5; g++)
                {
                    await SendAsync(BmsCanFrameFactory.CreateCellVoltageGroup(snapshot, g), writeBuffer, s, ct).ConfigureAwait(false);
                }
                nextCell = now + s.CellInterval;
            }

            if (now >= nextTemperature)
            {
                for (var g = 0; g < 3; g++)
                {
                    await SendAsync(BmsCanFrameFactory.CreateTemperatureGroup(snapshot, g), writeBuffer, s, ct).ConfigureAwait(false);
                }
                nextTemperature = now + s.TempInterval;
            }

            if (now >= nextBalancing)
            {
                await SendAsync(BmsCanFrameFactory.CreateBalancing(snapshot), writeBuffer, s, ct).ConfigureAwait(false);
                nextBalancing = now + s.BalancingInterval;
            }

            if (now >= nextDiagnostic)
            {
                await SendAsync(BmsCanFrameFactory.CreateDiagnostic(snapshot), writeBuffer, s, ct).ConfigureAwait(false);
                nextDiagnostic = now + s.DiagnosticInterval;
            }

            if (now >= nextHeartbeat)
            {
                await SendAsync(BmsCanFrameFactory.CreateHeartbeat(snapshot, heartbeatCounter++), writeBuffer, s, ct).ConfigureAwait(false);
                nextHeartbeat = now + s.HeartbeatInterval;
            }

            if (now >= nextRate)
            {
                var sent = Interlocked.Exchange(ref _sentSinceLastRate, 0);
                RateUpdated?.Invoke(this, new TransmissionRateEventArgs(sent));
                nextRate = now.AddSeconds(1);
            }

            try
            {
                await periodic.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task SendAsync(CanFrame frame, byte[] buffer, RuntimeSettings s, CancellationToken ct)
    {
        if (!_transport.IsOpen) return;

        int length;
        try
        {
            length = frame.TryRender(buffer, s.Format, s.AppendChecksum);
        }
        catch (Exception ex)
        {
            Interlocked.Increment(ref _totalErrors);
            Error?.Invoke(this, $"Render failed: {ex.Message}");
            return;
        }

        try
        {
            await _transport.WriteBytesAsync(new ReadOnlyMemory<byte>(buffer, 0, length), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _totalErrors);
            Error?.Invoke(this, ex.Message);
            return;
        }

        Interlocked.Increment(ref _totalFrames);
        Interlocked.Add(ref _totalBytes, length);
        Interlocked.Increment(ref _sentSinceLastRate);

        var sub = FrameTransmitted;
        if (sub is not null)
        {
            sub(this, new FrameTransmittedEventArgs(
                frame,
                frame.ToDisplayString(s.Format, s.AppendChecksum),
                BmsCanFrameFactory.Describe(frame, _simulationSettings),
                DateTimeOffset.Now));
        }
    }

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        if (_transport.CurrentOptions is null) return;

        if (!await _transport.TryReconnectAsync(ct).ConfigureAwait(false))
        {
            Error?.Invoke(this, $"Reconnect failed for {_transport.CurrentOptions.PortName}. Will retry.");
        }
    }

    /// <summary>Immutable per-loop snapshot of all mutable transmission settings.</summary>
    private sealed record RuntimeSettings(
        TimeSpan PackInterval,
        TimeSpan TempInterval,
        TimeSpan CellInterval,
        TimeSpan BalancingInterval,
        TimeSpan DiagnosticInterval,
        TimeSpan HeartbeatInterval,
        bool AppendChecksum,
        WireFormat Format)
    {
        public static RuntimeSettings From(TransmissionIntervals i, bool checksum, WireFormat format) =>
            new(
                Ms(i.PackStatusMs),
                Ms(i.TemperatureMs),
                Ms(i.CellVoltageMs),
                Ms(i.BalancingMs),
                Ms(i.DiagnosticMs),
                Ms(i.HeartbeatMs),
                checksum,
                format);

        public RuntimeSettings WithIntervals(TransmissionIntervals i) =>
            this with
            {
                PackInterval = Ms(i.PackStatusMs),
                TempInterval = Ms(i.TemperatureMs),
                CellInterval = Ms(i.CellVoltageMs),
                BalancingInterval = Ms(i.BalancingMs),
                DiagnosticInterval = Ms(i.DiagnosticMs),
                HeartbeatInterval = Ms(i.HeartbeatMs),
            };

        private static TimeSpan Ms(int v) => TimeSpan.FromMilliseconds(Math.Max(50, v));
    }
}
