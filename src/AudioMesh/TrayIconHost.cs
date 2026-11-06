using System;
using System.Linq;
using AudioMesh.ViewModels;
using Drawing = System.Drawing;
using WinForms = System.Windows.Forms;

namespace AudioMesh;

/// <summary>
/// The notification-area icon and its menu. Closing or minimizing the window hides
/// it here; the audio engine keeps running until Exit is chosen.
/// </summary>
public sealed class TrayIconHost : IDisposable
{
    private readonly MainWindow _window;
    private readonly MainViewModel _viewModel;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly WinForms.ToolStripMenuItem _routeItem;
    private readonly WinForms.ToolStripMenuItem _loginItem;
    private readonly WinForms.ToolStripMenuItem _autoRouteItem;
    private readonly Drawing.Icon _trayIcon;
    private readonly Drawing.Font _boldFont;
    private bool _balloonShown;
    private bool _disposed;

    public TrayIconHost(MainWindow window, MainViewModel viewModel)
    {
        _window = window;
        _viewModel = viewModel;
        _trayIcon = LoadIcon();
        _boldFont = new Drawing.Font(WinForms.SystemInformation.MenuFont, Drawing.FontStyle.Bold);

        _menu = new WinForms.ContextMenuStrip { ShowImageMargin = false };

        var open = new WinForms.ToolStripMenuItem("Open AudioMesh")
        {
            Font = _boldFont
        };
        open.Click += (_, _) => _window.ShowFromTray();

        _routeItem = new WinForms.ToolStripMenuItem("Stop routing");
        _routeItem.Click += (_, _) => _viewModel.ToggleCommand.Execute(null);

        _loginItem = new WinForms.ToolStripMenuItem("Start with Windows");
        _loginItem.Click += (_, _) => _viewModel.LaunchAtLogin = !_viewModel.LaunchAtLogin;

        _autoRouteItem = new WinForms.ToolStripMenuItem("Start routing on launch");
        _autoRouteItem.Click += (_, _) => _viewModel.AutoStart = !_viewModel.AutoStart;

        var exit = new WinForms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => _window.RequestExit();

        var updates = new WinForms.ToolStripMenuItem("Check for updates");
        updates.Click += (_, _) =>
        {
            _window.ShowFromTray();
            _viewModel.CheckForUpdatesCommand.Execute(null);
        };

        _menu.Items.Add(open);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_routeItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(_loginItem);
        _menu.Items.Add(_autoRouteItem);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(updates);
        _menu.Items.Add(new WinForms.ToolStripSeparator());
        _menu.Items.Add(exit);
        _menu.Opening += (_, _) => RefreshMenu();

        _icon = new WinForms.NotifyIcon
        {
            Icon = _trayIcon,
            Text = "AudioMesh — one stream, every speaker",
            Visible = true,
            ContextMenuStrip = _menu
        };
        _icon.DoubleClick += (_, _) => _window.ShowFromTray();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) _window.ShowFromTray();
        };
        _icon.BalloonTipClicked += (_, _) => _window.ShowFromTray();

        _viewModel.PropertyChanged += OnViewModelChanged;
        RefreshMenu();
    }

    public void ShowBalloonOnce()
    {
        if (_balloonShown) return;
        _balloonShown = true;
        _icon.ShowBalloonTip(3000,
            "AudioMesh is still running",
            "Double-click the tray icon to open it. Use Exit in this menu to quit.",
            WinForms.ToolTipIcon.Info);
    }

    private void RefreshMenu()
    {
        _routeItem.Text = _viewModel.IsRunning ? "Stop routing" : "Start routing";
        _routeItem.Enabled = _viewModel.Devices.Any(d => d.IsEnabled);
        _loginItem.Checked = _viewModel.LaunchAtLogin;
        _autoRouteItem.Checked = _viewModel.AutoStart;
    }

    private void OnViewModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsRunning)
            or nameof(MainViewModel.LaunchAtLogin)
            or nameof(MainViewModel.AutoStart))
        {
            RefreshMenu();
        }
    }

    private static Drawing.Icon LoadIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/AudioMesh.ico"));
            if (resource != null)
            {
                using (resource.Stream)
                    return new Drawing.Icon(resource.Stream);
            }
        }
        catch
        {
            // fall through to the executable icon
        }

        try
        {
            string? exe = Environment.ProcessPath;
            if (exe != null)
            {
                Drawing.Icon? icon = Drawing.Icon.ExtractAssociatedIcon(exe);
                if (icon != null) return icon;
            }
        }
        catch
        {
            // fall through to the stock icon
        }

        return (Drawing.Icon)Drawing.SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _viewModel.PropertyChanged -= OnViewModelChanged;
        _menu.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        _trayIcon.Dispose();
        _boldFont.Dispose();
    }
}
