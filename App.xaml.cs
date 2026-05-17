using CanBusSimulator.Configuration;
using CanBusSimulator.Serial;
using CanBusSimulator.Simulation;
using CanBusSimulator.Transmission;
using CanBusSimulator.ViewModels;
using CanBusSimulator.Views;
using Microsoft.UI.Xaml;

namespace CanBusSimulator;

/// <summary>
/// WinUI 3 application entry point. Wires shared services and shows the main window.
/// </summary>
public partial class App : Application
{
    private Window? _window;

    /// <summary>Creates the application and registers shared services.</summary>
    public App()
    {
        InitializeComponent();
    }

    /// <summary>The active main window of the application.</summary>
    public Window? MainWindow => _window;

    /// <inheritdoc />
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var configService = new ConfigService();
        var config = configService.Load();
        var transport = new SerialPortTransport();
        var simulator = new BmsDataSimulator(config.Simulation);
        var fileSource = new FileBmsDataSource();
        var provider = new BmsSnapshotProvider(simulator, fileSource);
        var transmissionService = new TransmissionService(
            transport,
            provider,
            config.Simulation,
            config.Intervals,
            config.Serial.AppendChecksumToWireFormat,
            config.Serial.WireFormat);

        var viewModel = new MainViewModel(
            configService,
            config,
            transport,
            simulator,
            fileSource,
            provider,
            transmissionService);

        _window = new MainWindow(viewModel);
        _window.Activate();
    }
}
