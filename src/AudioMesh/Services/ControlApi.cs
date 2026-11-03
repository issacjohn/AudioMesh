using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace AudioMesh.Services;

/// <summary>One playback endpoint as the phone sees it.</summary>
public sealed record ControlEndpoint(
    string Id,
    string Name,
    string Kind,
    bool Enabled,
    double Volume,
    bool Muted,
    int DelayMs,
    int BufferMs,
    int TargetBufferMs,
    double DriftPpm,
    double Level,
    bool Running,
    string? Error);

/// <summary>Snapshot returned by GET /api/state.</summary>
public sealed record ControlState(
    bool Running,
    string Source,
    bool PerfectSync,
    string BufferMode,
    IReadOnlyList<ControlEndpoint> Endpoints);

/// <summary>A command from POST /api/action. Exactly one of the value fields is set.</summary>
public sealed class ControlRequest
{
    public string Action { get; init; } = string.Empty;
    public string? Id { get; init; }
    public double? Number { get; init; }
    public string? Text { get; init; }
    public bool? Flag { get; init; }
}

public sealed record ControlResult(bool Ok, string? Error = null);

/// <summary>
/// Loopback-only HTTP API used by the DeskGadget / LiquidDeck module.
/// Deliberately hand-rolled on TcpListener: HttpListener needs a URL ACL on
/// Windows, and Kestrel would drag the ASP.NET runtime into the installer.
/// </summary>
public sealed class ControlApiServer : IDisposable
{
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxBodyBytes = 64 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly TcpListener _listener;
    private readonly string _token;
    private readonly Func<ControlState> _state;
    private readonly Func<ControlRequest, ControlResult> _handler;
    private readonly CancellationTokenSource _cts = new();
    private bool _disposed;

    public ControlApiServer(
        int port,
        string token,
        Func<ControlState> state,
        Func<ControlRequest, ControlResult> handler)
    {
        _token = token ?? string.Empty;
        _state = state;
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, port);
    }

    public bool Start()
    {
        try
        {
            _listener.Start();
        }
        catch (SocketException ex)
        {
            Debug.WriteLine($"AudioMesh control API could not bind: {ex.Message}");
            return false;
        }

        _ = Task.Run(AcceptLoop);
        return true;
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            _ = Task.Run(() => HandleClient(client));
        }
    }

    private async Task HandleClient(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                NetworkStream stream = client.GetStream();

                ParsedRequest? request = await ReadRequest(stream);
                if (request == null)
                {
                    await WriteJson(stream, 400, new ControlResult(false, "Malformed request"));
                    return;
                }

                if (_token.Length > 0 &&
                    (!request.Headers.TryGetValue("x-audiomesh-token", out string? supplied) ||
                     !string.Equals(supplied, _token, StringComparison.Ordinal)))
                {
                    await WriteJson(stream, 401, new ControlResult(false, "Unauthorized"));
                    return;
                }

                string path = request.Path;
                if (path == "/api/state" && request.Method == "GET")
                {
                    await WriteJson(stream, 200, _state());
                    return;
                }

                if (path == "/api/action" && request.Method == "POST")
                {
                    ControlRequest? command = ParseCommand(request.Body);
                    if (command == null)
                    {
                        await WriteJson(stream, 400, new ControlResult(false, "Invalid JSON"));
                        return;
                    }

                    ControlResult result;
                    try
                    {
                        result = _handler(command);
                    }
                    catch (Exception ex)
                    {
                        result = new ControlResult(false, ex.Message);
                    }

                    await WriteJson(stream, result.Ok ? 200 : 400, result);
                    return;
                }

                await WriteJson(stream, 404, new ControlResult(false, "Not found"));
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"AudioMesh control API request failed: {ex.Message}");
            }
        }
    }

    private static ControlRequest? ParseCommand(byte[] body)
    {
        if (body.Length == 0) return new ControlRequest();

        try
        {
            using JsonDocument document = JsonDocument.Parse(body);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            string action = root.TryGetProperty("action", out JsonElement actionElement) &&
                            actionElement.ValueKind == JsonValueKind.String
                ? actionElement.GetString() ?? string.Empty
                : string.Empty;

            string? id = root.TryGetProperty("id", out JsonElement idElement) &&
                         idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null;

            double? number = null;
            string? text = null;
            bool? flag = null;

            if (root.TryGetProperty("value", out JsonElement value))
            {
                switch (value.ValueKind)
                {
                    case JsonValueKind.Number:
                        number = value.GetDouble();
                        break;
                    case JsonValueKind.String:
                        text = value.GetString();
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        flag = value.GetBoolean();
                        break;
                }
            }

            return new ControlRequest
            {
                Action = action,
                Id = id,
                Number = number,
                Text = text,
                Flag = flag
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ParsedRequest(
        string Method,
        string Path,
        Dictionary<string, string> Headers,
        byte[] Body);

    private static async Task<ParsedRequest?> ReadRequest(NetworkStream stream)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(MaxHeaderBytes);
        try
        {
            int used = 0;
            int headerEnd = -1;

            while (used < MaxHeaderBytes)
            {
                int read = await stream.ReadAsync(buffer.AsMemory(used, MaxHeaderBytes - used));
                if (read == 0) return null;
                used += read;

                headerEnd = IndexOfHeaderEnd(buffer, used);
                if (headerEnd >= 0) break;
            }

            if (headerEnd < 0) return null;

            string headerText = Encoding.ASCII.GetString(buffer, 0, headerEnd);
            string[] lines = headerText.Split("\r\n", StringSplitOptions.None);
            if (lines.Length == 0) return null;

            string[] requestLine = lines[0].Split(' ');
            if (requestLine.Length < 2) return null;

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                int colon = lines[i].IndexOf(':');
                if (colon <= 0) continue;
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }

            int contentLength = 0;
            if (headers.TryGetValue("Content-Length", out string? lengthText) &&
                (!int.TryParse(lengthText, out contentLength) || contentLength < 0 || contentLength > MaxBodyBytes))
            {
                return null;
            }

            byte[] body = new byte[contentLength];
            int copied = Math.Min(contentLength, used - (headerEnd + 4));
            if (copied > 0) Array.Copy(buffer, headerEnd + 4, body, 0, copied);

            int offset = copied;
            while (offset < contentLength)
            {
                int read = await stream.ReadAsync(body.AsMemory(offset, contentLength - offset));
                if (read == 0) break;
                offset += read;
            }

            string path = requestLine[1];
            int query = path.IndexOf('?');
            if (query >= 0) path = path[..query];

            return new ParsedRequest(requestLine[0].ToUpperInvariant(), path, headers, body);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static int IndexOfHeaderEnd(byte[] buffer, int length)
    {
        for (int i = 0; i + 3 < length; i++)
        {
            if (buffer[i] == '\r' && buffer[i + 1] == '\n' &&
                buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteJson(NetworkStream stream, int status, object payload)
    {
        byte[] body = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions);
        string reason = status switch
        {
            200 => "OK",
            400 => "Bad Request",
            401 => "Unauthorized",
            404 => "Not Found",
            _ => "Error"
        };

        string head =
            $"HTTP/1.1 {status} {reason}\r\n" +
            "Content-Type: application/json; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _cts.Cancel(); } catch { /* ignore */ }
        try { _listener.Stop(); } catch { /* ignore */ }
        _cts.Dispose();
    }
}

/// <summary>Port and token shared with the phone. Kept out of settings.json so a
/// token can be created on first launch without rewriting the user's device list.</summary>
public sealed class ControlConfig
{
    public int Port { get; set; } = 47823;
    public string Token { get; set; } = string.Empty;
}

public static class ControlConfigStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string FilePath
    {
        get
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "AudioMesh");
            Directory.CreateDirectory(directory);
            return Path.Combine(directory, "control.json");
        }
    }

    public static ControlConfig LoadOrCreate()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                ControlConfig? existing = JsonSerializer.Deserialize<ControlConfig>(File.ReadAllText(FilePath));
                if (existing != null)
                {
                    bool changed = false;
                    if (existing.Port is <= 0 or > 65535)
                    {
                        existing.Port = 47823;
                        changed = true;
                    }

                    if (string.IsNullOrWhiteSpace(existing.Token))
                    {
                        existing.Token = NewToken();
                        changed = true;
                    }

                    if (changed) Save(existing);
                    return existing;
                }
            }
        }
        catch
        {
            // fall through and recreate
        }

        var created = new ControlConfig { Port = 47823, Token = NewToken() };
        Save(created);
        return created;
    }

    public static void Save(ControlConfig config)
    {
        try
        {
            string path = FilePath;
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(config, Options));
            File.Move(temp, path, true);
        }
        catch
        {
            // the API is optional; never fail the app over it
        }
    }

    private static string NewToken() =>
        Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}
