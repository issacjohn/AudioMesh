using System.Diagnostics;

namespace AudioMesh.Services;

/// <summary>Opens a trusted URL in the user's default browser.</summary>
public static class ExternalLink
{
    public static void Open(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // A missing browser or blocked shell action must not break the app.
        }
    }
}
