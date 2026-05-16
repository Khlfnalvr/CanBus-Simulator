using CanBusSimulator.Models;

namespace CanBusSimulator.Simulation;

/// <summary>
/// Selects between generated BMS data and file-replayed BMS data at runtime.
/// </summary>
public sealed class BmsSnapshotProvider : IBmsSnapshotSource
{
    private readonly object _syncRoot = new();

    /// <summary>
    /// Creates a source switcher for generated and file-backed snapshots.
    /// </summary>
    public BmsSnapshotProvider(BmsDataSimulator simulator, FileBmsDataSource fileSource)
    {
        Simulator = simulator;
        FileSource = fileSource;
    }

    /// <summary>
    /// Automatic/manual generated simulator.
    /// </summary>
    public BmsDataSimulator Simulator { get; }

    /// <summary>
    /// File replay source.
    /// </summary>
    public FileBmsDataSource FileSource { get; }

    /// <summary>
    /// When true and a file is loaded, snapshots are read from the file replay source.
    /// </summary>
    public bool UseFileSource
    {
        get
        {
            lock (_syncRoot)
            {
                return _useFileSource;
            }
        }
        set
        {
            lock (_syncRoot)
            {
                _useFileSource = value;
            }
        }
    }

    private bool _useFileSource;

    /// <inheritdoc />
    public BmsSnapshot Update(TimeSpan elapsed)
    {
        lock (_syncRoot)
        {
            if (_useFileSource && FileSource.HasRows)
            {
                return FileSource.Update(elapsed);
            }
        }

        return Simulator.Update(elapsed);
    }
}
