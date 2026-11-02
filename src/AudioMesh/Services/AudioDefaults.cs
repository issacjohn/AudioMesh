using System;
using System.Runtime.InteropServices;

namespace AudioMesh.Services;

/// <summary>
/// Changes the Windows default playback endpoint.
///
/// This uses the same undocumented <c>IPolicyConfig</c> interface that the Sound
/// control panel itself uses. It is the only way to move the system default without
/// asking the user to click through Settings, and it is what "Perfect sync" needs:
/// the default endpoint is the one that gets captured, so it must be an output that
/// is not one of the speakers we want to drive.
/// </summary>
public static class AudioDefaults
{
    private static readonly Guid PolicyConfigClient = new("870af99c-171d-4f9e-af0d-e63df40c2bc9");

    /// <summary>Sets the endpoint as the default for console, multimedia and communications.</summary>
    public static bool SetDefault(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) return false;

        object? com = null;
        try
        {
            Type? type = Type.GetTypeFromCLSID(PolicyConfigClient);
            if (type == null) return false;

            com = Activator.CreateInstance(type);
            if (com is not IPolicyConfig client) return false;

            for (int role = 0; role <= 2; role++)
                client.SetDefaultEndpoint(deviceId, role);

            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (com != null)
            {
                try { Marshal.ReleaseComObject(com); } catch { /* ignore */ }
            }
        }
    }

    [ComImport]
    [Guid("f8679f50-850a-41cf-9c72-430f290290c8")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string name, out IntPtr format);
        [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out IntPtr format);
        [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name);
        [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr endpoint, IntPtr mix);
        [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool isDefault, out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string name, ref long period);
        [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string name, out int mode);
        [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string name, ref int mode);
        [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr key, IntPtr value);
        [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr key, IntPtr value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string name, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string name, [MarshalAs(UnmanagedType.Bool)] bool visible);
    }
}
