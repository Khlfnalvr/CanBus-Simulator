using System.Text.Json;

namespace CanBusSimulator.Configuration;

/// <summary>
/// Loads and saves simulator settings from appsettings.json beside the executable.
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly string _configPath;

    /// <summary>
    /// Creates a configuration service using the executable directory.
    /// </summary>
    public ConfigService()
        : this(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
    {
    }

    /// <summary>
    /// Creates a configuration service using a specific JSON file path.
    /// </summary>
    public ConfigService(string configPath)
    {
        _configPath = configPath;
    }

    /// <summary>
    /// Reads configuration from disk and falls back to safe defaults when missing or invalid.
    /// </summary>
    public AppConfig Load()
    {
        try
        {
            if (!File.Exists(_configPath))
            {
                return new AppConfig();
            }

            var json = File.ReadAllText(_configPath);
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch
        {
            return new AppConfig();
        }
    }

    /// <summary>
    /// Saves the latest runtime configuration. Errors are returned as text for UI logging.
    /// </summary>
    public string? Save(AppConfig config)
    {
        try
        {
            var json = JsonSerializer.Serialize(config, JsonOptions);
            File.WriteAllText(_configPath, json);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }
}
