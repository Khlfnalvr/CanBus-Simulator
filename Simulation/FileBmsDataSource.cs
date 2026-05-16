using CanBusSimulator.Models;

namespace CanBusSimulator.Simulation;

/// <summary>
/// Replays BMS snapshots loaded from a CSV or Excel file.
/// </summary>
public sealed class FileBmsDataSource : IBmsSnapshotSource
{
    private readonly object _syncRoot = new();
    private IReadOnlyList<BmsSnapshot> _rows = Array.Empty<BmsSnapshot>();
    private int _currentIndex;
    private TimeSpan _elapsedSinceAdvance = TimeSpan.Zero;
    private TimeSpan _rowInterval = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Source file path for the loaded rows.
    /// </summary>
    public string SourcePath { get; private set; } = string.Empty;

    /// <summary>
    /// True when replay wraps back to row one at end-of-file.
    /// </summary>
    public bool LoopFile { get; set; } = true;

    /// <summary>
    /// True when at least one file row has been loaded.
    /// </summary>
    public bool HasRows
    {
        get
        {
            lock (_syncRoot)
            {
                return _rows.Count > 0;
            }
        }
    }

    /// <summary>
    /// Total rows currently loaded.
    /// </summary>
    public int RowCount
    {
        get
        {
            lock (_syncRoot)
            {
                return _rows.Count;
            }
        }
    }

    /// <summary>
    /// One-based current row number in the replay sequence.
    /// </summary>
    public int CurrentRowNumber
    {
        get
        {
            lock (_syncRoot)
            {
                return _rows.Count == 0 ? 0 : _currentIndex + 1;
            }
        }
    }

    /// <summary>
    /// Applies newly loaded file rows and resets replay to the first data row.
    /// </summary>
    public void Load(SimulationFileData data)
    {
        lock (_syncRoot)
        {
            _rows = data.Rows;
            SourcePath = data.SourcePath;
            _currentIndex = 0;
            _elapsedSinceAdvance = TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Sets the row advance interval in milliseconds.
    /// </summary>
    public void SetRowInterval(int intervalMs)
    {
        lock (_syncRoot)
        {
            _rowInterval = TimeSpan.FromMilliseconds(Math.Max(1000, intervalMs));
        }
    }

    /// <inheritdoc />
    public BmsSnapshot Update(TimeSpan elapsed)
    {
        lock (_syncRoot)
        {
            if (_rows.Count == 0)
            {
                throw new InvalidOperationException("No simulation file rows are loaded.");
            }

            _elapsedSinceAdvance += elapsed;
            while (_elapsedSinceAdvance >= _rowInterval && _rows.Count > 1)
            {
                _elapsedSinceAdvance -= _rowInterval;
                if (_currentIndex < _rows.Count - 1)
                {
                    _currentIndex++;
                    continue;
                }

                if (LoopFile)
                {
                    _currentIndex = 0;
                    continue;
                }

                _elapsedSinceAdvance = TimeSpan.Zero;
                break;
            }

            var row = _rows[_currentIndex];
            return row with { Timestamp = DateTimeOffset.Now };
        }
    }
}
