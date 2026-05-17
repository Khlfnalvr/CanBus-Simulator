using CanBusSimulator.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Windowing;
using Windows.Storage.Pickers;
using Windows.UI;
using WinRT.Interop;

namespace CanBusSimulator.Views;

/// <summary>
/// Main application window. Hosts the simulator UI, runs the file picker, and
/// owns the custom title bar (Mica backdrop, theme-aware caption buttons, and
/// the inline theme toggle that sits next to the system action buttons).
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>The bound ViewModel exposed to XAML.</summary>
    public MainViewModel ViewModel { get; }

    private bool _autoLoadAttempted;

    /// <summary>Creates the window and binds it to the supplied ViewModel.</summary>
    public MainWindow(MainViewModel viewModel)
    {
        ViewModel = viewModel;
        InitializeComponent();

        Title = "CAN Bus BMS Simulator";

        ConfigureWindow();
        ApplyInitialTheme();
        SetupCustomTitleBar();

        ((FrameworkElement)Content).ActualThemeChanged += (_, _) =>
        {
            UpdateCaptionButtonColors();
            UpdateThemeIcon();
        };

        Closed += async (_, _) =>
        {
            ViewModel.SaveConfig();
            await ViewModel.ShutdownAsync();
        };

        Activated += async (_, _) => await TryAutoLoadFileAsync();
    }

    private void ConfigureWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.Resize(new Windows.Graphics.SizeInt32(1100, 820));

        // Mica backdrop — automatically follows the system light/dark theme on Windows 11.
        try
        {
            SystemBackdrop = new MicaBackdrop();
        }
        catch
        {
            // Mica not supported (older Windows); falls back to default backdrop.
        }
    }

    private void ApplyInitialTheme()
    {
        if (Content is FrameworkElement root)
        {
            root.RequestedTheme = ViewModel.ResolveStartupTheme();
        }
    }

    private void SetupCustomTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        appWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;

        UpdateCaptionButtonColors();
        UpdateThemeIcon();
        UpdateCaptionReservedWidth();

        // Keep the spacer column in sync with the system caption-buttons width
        // (it can change on DPI updates).
        AppTitleBar.SizeChanged += (_, _) => UpdateCaptionReservedWidth();
    }

    private void UpdateCaptionReservedWidth()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var insetPx = appWindow.TitleBar.RightInset;
        if (insetPx <= 0)
        {
            return;
        }

        var scale = (this.Content as FrameworkElement)?.XamlRoot?.RasterizationScale ?? 1.0;
        var dipWidth = insetPx / scale;
        CaptionButtonsColumn.Width = new GridLength(dipWidth);
    }

    private void UpdateCaptionButtonColors()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        var appWindow = AppWindow.GetFromWindowId(windowId);
        var titleBar = appWindow.TitleBar;

        titleBar.BackgroundColor = Colors.Transparent;
        titleBar.InactiveBackgroundColor = Colors.Transparent;
        titleBar.ButtonBackgroundColor = Colors.Transparent;
        titleBar.ButtonInactiveBackgroundColor = Colors.Transparent;

        var theme = ((FrameworkElement)Content).ActualTheme;
        if (theme == ElementTheme.Dark)
        {
            titleBar.ForegroundColor = Colors.White;
            titleBar.ButtonForegroundColor = Colors.White;
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x9A, 0x9A, 0x9A);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonHoverForegroundColor = Colors.White;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF);
            titleBar.ButtonPressedForegroundColor = Colors.White;
        }
        else
        {
            titleBar.ForegroundColor = Colors.Black;
            titleBar.ButtonForegroundColor = Colors.Black;
            titleBar.ButtonInactiveForegroundColor = Color.FromArgb(0xFF, 0x60, 0x60, 0x60);
            titleBar.ButtonHoverBackgroundColor = Color.FromArgb(0x22, 0x00, 0x00, 0x00);
            titleBar.ButtonHoverForegroundColor = Colors.Black;
            titleBar.ButtonPressedBackgroundColor = Color.FromArgb(0x44, 0x00, 0x00, 0x00);
            titleBar.ButtonPressedForegroundColor = Colors.Black;
        }
    }

    private void UpdateThemeIcon()
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        // Show current state: sun for light, moon for dark.
        ThemeIcon.Glyph = root.ActualTheme == ElementTheme.Dark
            ? ""  // QuietHours / moon
            : ""; // Sunny / sun
    }

    private void OnThemeToggleClick(object sender, RoutedEventArgs e)
    {
        if (Content is not FrameworkElement root)
        {
            return;
        }

        var next = root.ActualTheme == ElementTheme.Dark
            ? ElementTheme.Light
            : ElementTheme.Dark;

        root.RequestedTheme = next;
        ViewModel.SetTheme(next);
    }

    private void ContentScroller_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Force the inner Grid to be at least viewport-height. When content fits
        // the * row (Log card) stretches to fill — no empty gap. When content
        // overflows, the Grid grows beyond viewport and the ScrollViewer scrolls.
        var paddingV = ContentScroller.Padding.Top + ContentScroller.Padding.Bottom;
        ContentGrid.MinHeight = Math.Max(0, e.NewSize.Height - paddingV);
    }

    private async void OnBrowseFileClick(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker
        {
            ViewMode = PickerViewMode.List,
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary
        };
        picker.FileTypeFilter.Add(".csv");
        picker.FileTypeFilter.Add(".tsv");
        picker.FileTypeFilter.Add(".xlsx");
        picker.FileTypeFilter.Add(".xlsm");

        var hwnd = WindowNative.GetWindowHandle(this);
        InitializeWithWindow.Initialize(picker, hwnd);

        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        await ViewModel.LoadSimulationFileAsync(file.Path);
    }

    private async Task TryAutoLoadFileAsync()
    {
        if (_autoLoadAttempted)
        {
            return;
        }

        _autoLoadAttempted = true;

        if (!ViewModel.UseFileData || string.IsNullOrWhiteSpace(ViewModel.SimulationFilePath))
        {
            return;
        }

        var resolved = ResolveSimulationFilePath(ViewModel.SimulationFilePath);
        if (File.Exists(resolved))
        {
            await ViewModel.LoadSimulationFileAsync(resolved);
        }
    }

    private static string ResolveSimulationFilePath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
        {
            return filePath;
        }

        return Path.Combine(AppContext.BaseDirectory, filePath);
    }
}
