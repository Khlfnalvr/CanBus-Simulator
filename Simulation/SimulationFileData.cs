using CanBusSimulator.Models;

namespace CanBusSimulator.Simulation;

/// <summary>
/// Parsed simulation rows and non-fatal warnings from a CSV or Excel file.
/// </summary>
public sealed record SimulationFileData(
    string SourcePath,
    IReadOnlyList<BmsSnapshot> Rows,
    IReadOnlyList<string> Warnings);
