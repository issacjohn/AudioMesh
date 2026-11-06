using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace AudioMesh.Services;

public sealed record UpdateInfo(
    Version Version,
    string Tag,
    string Notes,
    string ReleaseUrl,
    string? SetupUrl);

/// <summary>
/// Checks the public GitHub release feed and downloads the installer attached to it.
/// No account, no token: a plain unauthenticated GET of the latest release.
/// </summary>
public static class UpdateService
{
    private const string DefaultFeed =
        "https://api.github.com/repos/issacjohn/AudioMesh/releases/latest";

    public static Version CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version ?? new Version(1, 0, 0);

    private static string FeedUrl
    {
        get
        {
            string? custom = Environment.GetEnvironmentVariable("AUDIOMESH_UPDATE_URL");
            return string.IsNullOrWhiteSpace(custom) ? DefaultFeed : custom;
        }
    }

    public static async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AudioMesh/" + CurrentVersion);

        string json = await http.GetStringAsync(FeedUrl, cancellationToken);

        using JsonDocument document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;

        string tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
            ? tagElement.GetString() ?? string.Empty
            : string.Empty;

        if (!Version.TryParse(tag.TrimStart('v', 'V'), out Version? latest))
            return null;

        string notes = root.TryGetProperty("body", out JsonElement bodyElement)
            ? bodyElement.GetString() ?? string.Empty
            : string.Empty;

        string releaseUrl = root.TryGetProperty("html_url", out JsonElement urlElement)
            ? urlElement.GetString() ?? string.Empty
            : string.Empty;

        string? setupUrl = null;
        if (root.TryGetProperty("assets", out JsonElement assets) &&
            assets.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = asset.TryGetProperty("name", out JsonElement nameElement)
                    ? nameElement.GetString() ?? string.Empty
                    : string.Empty;

                if (!name.StartsWith("AudioMeshSetup-", StringComparison.OrdinalIgnoreCase) ||
                    !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    continue;

                setupUrl = asset.TryGetProperty("browser_download_url", out JsonElement download)
                    ? download.GetString()
                    : null;

                if (!string.IsNullOrEmpty(setupUrl)) break;
            }
        }

        return new UpdateInfo(Normalize(latest), tag, notes, releaseUrl, setupUrl);
    }

    public static async Task<string> DownloadAsync(
        UpdateInfo info,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(info.SetupUrl))
            throw new InvalidOperationException("This release has no installer attached.");

        string path = Path.Combine(Path.GetTempPath(), $"AudioMesh-Setup-{info.Version}.exe");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AudioMesh/" + CurrentVersion);

        using HttpResponseMessage response = await http.GetAsync(
            info.SetupUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using FileStream file = File.Create(path);
        await response.Content.CopyToAsync(file, cancellationToken);
        return path;
    }

    private static Version Normalize(Version version) =>
        new(version.Major, version.Minor, Math.Max(version.Build, 0));
}
