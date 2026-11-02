using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;

namespace AudioMesh;

public partial class App : System.Windows.Application
{
    private const string ActivationEventName = "AudioMesh.Activate";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _activationEvent;
    private Thread? _activationThread;

    protected override void OnStartup(StartupEventArgs e)
    {
        _instanceMutex = new Mutex(true, "AudioMesh.SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            ActivateExistingInstance();
            Shutdown();
            return;
        }

        base.OnStartup(e);

        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationThread = new Thread(ActivationLoop)
        {
            IsBackground = true,
            Name = "AudioMesh activation"
        };
        _activationThread.Start();

        DispatcherUnhandledException += (_, args) =>
        {
            System.Windows.MessageBox.Show(args.Exception.Message, "AudioMesh", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
    }

    private void ActivationLoop()
    {
        while (true)
        {
            try
            {
                if (_activationEvent == null || !_activationEvent.WaitOne()) return;

                Dispatcher.Invoke(() =>
                {
                    if (MainWindow is MainWindow window)
                    {
                        window.ShowFromTray();
                    }
                    else
                    {
                        // The window can still be starting when a second launch arrives.
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            if (MainWindow is MainWindow pending) pending.ShowFromTray();
                        }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    }
                });
            }
            catch
            {
                return;
            }
        }
    }

    private static void ActivateExistingInstance()
    {
        if (SignalExistingInstance()) return;

        foreach (Process process in Process.GetProcessesByName("AudioMesh"))
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;

                IntPtr hwnd = process.MainWindowHandle;
                if (hwnd == IntPtr.Zero) continue;

                ShowWindow(hwnd, 9); // SW_RESTORE
                SetForegroundWindow(hwnd);
                return;
            }
        }

        System.Windows.MessageBox.Show("AudioMesh is already running.", "AudioMesh",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static bool SignalExistingInstance()
    {
        for (int attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                using EventWaitHandle activation = EventWaitHandle.OpenExisting(ActivationEventName);
                activation.Set();
                return true;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(100);
            }
            catch
            {
                return false;
            }
        }

        return false;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    protected override void OnExit(ExitEventArgs e)
    {
        try { _activationEvent?.Set(); } catch { /* shutting down */ }
        _activationEvent?.Dispose();
        _activationEvent = null;

        try { _instanceMutex?.ReleaseMutex(); } catch { /* not owner */ }
        _instanceMutex?.Dispose();
        _instanceMutex = null;
        base.OnExit(e);
    }
}
