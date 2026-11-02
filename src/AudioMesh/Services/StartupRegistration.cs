using System;
using System.Diagnostics;
using Microsoft.Win32;

namespace AudioMesh.Services;

/// <summary>
/// Adds or removes the per-user Run entry that launches AudioMesh at sign-in.
/// The app starts hidden in the tray; routing still follows the Start routing setting.
/// </summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "AudioMesh";

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value && value.Length > 0;
            }
            catch
            {
                return false;
            }
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key == null) return;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return;
            }

            string? exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
                exe = Process.GetCurrentProcess().MainModule?.FileName;

            if (string.IsNullOrWhiteSpace(exe)) return;

            key.SetValue(ValueName, $"\"{exe}\" --autostart", RegistryValueKind.String);
        }
        catch
        {
            // A locked or restricted Run key must not break the app.
        }
    }
}
