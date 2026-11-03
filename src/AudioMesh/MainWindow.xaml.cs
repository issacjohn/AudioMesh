using System;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using AudioMesh.ViewModels;

namespace AudioMesh;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;
    private TrayIconHost? _tray;
    private bool _exiting;
    private bool _hiding;
    private bool _wasMaximized;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel();
        _viewModel.ShowRequested += ShowFromTray;
        _viewModel.StartControlApi();
        DataContext = _viewModel;
        ApplyWindowPlacement();

        _tray = new TrayIconHost(this, _viewModel);

        string[] args = Environment.GetCommandLineArgs();
        bool startHidden = args.Any(a =>
            a.Equals("--autostart", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--tray", StringComparison.OrdinalIgnoreCase));

        if (startHidden)
            Loaded += (_, _) => HideToTray(showBalloon: false);

        Closed += (_, _) =>
        {
            _tray?.Dispose();
            _tray = null;
            _viewModel.Dispose();
        };

        StateChanged += (_, _) =>
        {
            UpdateCaptionGlyph();
            if (WindowState == WindowState.Minimized && !_hiding && !_exiting)
                HideToTray();
        };

        Closing += OnClosing;

        if (System.Windows.Application.Current != null)
            System.Windows.Application.Current.SessionEnding += OnSessionEnding;
    }

    /// <summary>Brings the window back from the tray, including after a second launch.</summary>
    public void ShowFromTray()
    {
        if (!IsVisible)
        {
            ShowInTaskbar = true;
            Show();
        }

        ShowInTaskbar = true;

        if (WindowState == WindowState.Minimized)
            WindowState = _wasMaximized ? WindowState.Maximized : WindowState.Normal;

        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    public void HideToTray(bool showBalloon = true, bool savePlacement = true)
    {
        if (_exiting || _hiding) return;

        _hiding = true;
        try
        {
            _wasMaximized = WindowState == WindowState.Maximized;
            if (savePlacement) SavePlacement();

            ShowInTaskbar = false;
            Hide();
            if (showBalloon) _tray?.ShowBalloonOnce();
        }
        finally
        {
            _hiding = false;
        }
    }

    public void RequestExit()
    {
        _exiting = true;
        Close();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePlacement();

        if (_exiting) return;

        // Closing the window keeps the audio engine alive in the tray.
        e.Cancel = true;
        HideToTray(savePlacement: false);
    }

    private void OnSessionEnding(object sender, SessionEndingCancelEventArgs e)
    {
        _exiting = true;
    }

    private void SavePlacement()
    {
        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        _viewModel.SetWindowPlacement(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
            WindowState == WindowState.Maximized);
        _viewModel.Save();
    }

    private void ApplyWindowPlacement()
    {
        (double Left, double Top, double Width, double Height, bool Maximized) placement =
            _viewModel.GetWindowPlacement();

        if (placement.Width < MinWidth || placement.Height < MinHeight) return;

        double left = SystemParameters.VirtualScreenLeft;
        double top = SystemParameters.VirtualScreenTop;
        double right = left + SystemParameters.VirtualScreenWidth;
        double bottom = top + SystemParameters.VirtualScreenHeight;

        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = Math.Min(placement.Width, SystemParameters.VirtualScreenWidth);
        Height = Math.Min(placement.Height, SystemParameters.VirtualScreenHeight);
        Left = Math.Clamp(placement.Left, left - Width + 120, right - 120);
        Top = Math.Clamp(placement.Top, top, bottom - 80);

        if (placement.Maximized)
        {
            _wasMaximized = true;
            WindowState = WindowState.Maximized;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryRoundWindow();
    }

    private void TryRoundWindow()
    {
        try
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
        }
        catch
        {
            // older Windows: keep the square frame
        }
    }

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => HideToTray();

    private void Maximize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void UpdateCaptionGlyph()
    {
        if (MaximizeButton == null) return;

        bool maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "Restore" : "Maximize";
    }
}
