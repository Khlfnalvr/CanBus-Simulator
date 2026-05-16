using CanBusSimulator.Configuration;
using CanBusSimulator.Serial;
using CanBusSimulator.UI;

namespace CanBusSimulator;

internal static class Program
{
    /// <summary>
    /// Starts the WinForms application and wires shared services.
    /// </summary>
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        var configService = new ConfigService();
        var config = configService.Load();
        using var transport = new Win32SerialTransport();

        Application.Run(new MainForm(configService, config, transport));
    }
}
