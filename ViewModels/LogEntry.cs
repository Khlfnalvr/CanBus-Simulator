namespace CanBusSimulator.ViewModels;

/// <summary>
/// One row in the transmission log shown in the UI.
/// </summary>
public sealed record LogEntry(string Time, string WireLine, string Decoded);
