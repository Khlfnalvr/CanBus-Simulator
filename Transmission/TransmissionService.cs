using System.Text;
using CanBusSimulator.Serial;
using CanBusSimulator.Simulation;

namespace CanBusSimulator.Transmission;

/// <summary>
/// Periodically serializes the current BMS snapshot as a JSON line
/// (see <see cref="BmsJsonFormatter"/>) and writes it to the serial transport.
/// </summary>
public sealed class TransmissionService : IDisposable
{
    private readonly object _stateGate = new();
    private readonly ISerialTransport _transport;
    private readonly IBmsSnapshotSource _snapshotSource;
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _workerTask;

    private volatile RuntimeSettings _settings;

    private long _totalFrames;
    private long _totalBytes;
    private long _totalErrors;
    private DateTimeOffset _startedAt;
    private int _sentSinceLastRate;

    public TransmissionService(
        ISerialTransport transport,
        IBmsSnapshotSource snapshotSource,
        TransmissionIntervals intervals)
    {
        _transport = transport;
        _snapshotSource = snapshotSource;
        _settings = RuntimeSettings.From(intervals);
    }

    public event EventHandler<FrameTransmittedEventArgs>? FrameTransmitted;
    public event EventHandler<string>? Error;
    public event EventHandler<TransmissionRateEventArgs>? RateUpdated;

    public bool IsRunning
    {
        get
        {
            lock (_stateGate)
                return _workerTask is { IsCompleted: false };
        }
    }

    public long TotalFrames => Interlocked.Read(ref _totalFrames);
    public long TotalBytes  => Interlocked.Read(ref _totalBytes);
    public long TotalErrors => Interlocked.Read(ref _totalErrors);
    public DateTimeOffset? StartedAt => _startedAt == default ? null : _startedAt;

    public void Start()
    {
        lock (_stateGate)
        {
            if (_workerTask is { IsCompleted: false }) return;
            _cancellationTokenSource = new CancellationTokenSource();
            _startedAt   = DateTimeOffset.UtcNow;
            _workerTask  = Task.Run(() => RunAsync(_cancellationTokenSource.Token));
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cts;
        Task? task;
        lock (_stateGate) { cts = _cancellationTokenSource; task = _workerTask; }

        if (cts is null || task is null) return;

        cts.Cancel();
        try   { await task.ConfigureAwait(false); }
        catch (OperationCanceledException) { }
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

    public void UpdateIntervals(TransmissionIntervals intervals) => _settings = _settings.WithIntervals(intervals);

    public void ResetStatistics()
    {
        Interlocked.Exchange(ref _totalFrames, 0);
        Interlocked.Exchange(ref _totalBytes,  0);
        Interlocked.Exchange(ref _totalErrors, 0);
        _startedAt = IsRunning ? DateTimeOffset.UtcNow : default;
    }

    public void Dispose() => StopAsync().GetAwaiter().GetResult();

    // ── Worker loop ───────────────────────────────────────────────────────

    private async Task RunAsync(CancellationToken ct)
    {
        var json = new StringBuilder(512);

        var lastUpdate    = DateTimeOffset.UtcNow;
        var nextSend      = lastUpdate;
        var nextRate      = lastUpdate.AddSeconds(1);
        var nextReconnect = DateTimeOffset.MinValue;

        using var periodic = new PeriodicTimer(TimeSpan.FromMilliseconds(10));

        while (!ct.IsCancellationRequested)
        {
            var now     = DateTimeOffset.UtcNow;
            var elapsed = now - lastUpdate;
            lastUpdate  = now;
            var snapshot = _snapshotSource.Update(elapsed);
            var s = _settings;

            if (!_transport.IsOpen && now >= nextReconnect)
            {
                await TryReconnectAsync(ct).ConfigureAwait(false);
                nextReconnect = now.AddSeconds(1);
            }

            if (now >= nextSend)
            {
                json.Clear();
                BmsJsonFormatter.Format(snapshot, json);
                var line = json.ToString();
                var bytes = Encoding.UTF8.GetBytes(line);

                if (await TrySendAsync(bytes, ct).ConfigureAwait(false))
                {
                    Interlocked.Increment(ref _totalFrames);
                    Interlocked.Add(ref _totalBytes, bytes.Length);
                    Interlocked.Increment(ref _sentSinceLastRate);

                    var sub = FrameTransmitted;
                    if (sub is not null)
                    {
                        sub(this, new FrameTransmittedEventArgs(
                            line.TrimEnd('\r', '\n'),
                            BmsJsonFormatter.Describe(snapshot),
                            DateTimeOffset.Now));
                    }
                }

                nextSend = now + s.SendInterval;
            }

            if (now >= nextRate)
            {
                var sent = Interlocked.Exchange(ref _sentSinceLastRate, 0);
                RateUpdated?.Invoke(this, new TransmissionRateEventArgs(sent));
                nextRate = now.AddSeconds(1);
            }

            try   { await periodic.WaitForNextTickAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<bool> TrySendAsync(byte[] bytes, CancellationToken ct)
    {
        if (!_transport.IsOpen) return false;

        try
        {
            await _transport.WriteBytesAsync(bytes, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
        {
            Interlocked.Increment(ref _totalErrors);
            Error?.Invoke(this, ex.Message);
            return false;
        }
    }

    private async Task TryReconnectAsync(CancellationToken ct)
    {
        if (_transport.CurrentOptions is null) return;
        if (!await _transport.TryReconnectAsync(ct).ConfigureAwait(false))
            Error?.Invoke(this, $"Reconnect failed for {_transport.CurrentOptions.PortName}. Will retry.");
    }

    // ── Immutable settings snapshot ───────────────────────────────────────

    private sealed record RuntimeSettings(TimeSpan SendInterval)
    {
        public static RuntimeSettings From(TransmissionIntervals i) => new(Ms(i.SendIntervalMs));

        public RuntimeSettings WithIntervals(TransmissionIntervals i) =>
            this with { SendInterval = Ms(i.SendIntervalMs) };

        private static TimeSpan Ms(int v) => TimeSpan.FromMilliseconds(Math.Max(50, v));
    }
}
