using CanBusSimulator.Models;

namespace CanBusSimulator.Simulation;

/// <summary>
/// Provides the latest BMS snapshot for the CAN transmission scheduler.
/// </summary>
public interface IBmsSnapshotSource
{
    /// <summary>
    /// Advances the source by elapsed time and returns the current snapshot.
    /// </summary>
    BmsSnapshot Update(TimeSpan elapsed);
}
