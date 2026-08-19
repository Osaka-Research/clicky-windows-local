using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using ClickyWindows.Models;

namespace ClickyWindows.Services;

/// <summary>
/// Talks to a Clicky.Server instance instead of running Whisper/Claude on this machine --
/// for a client with no GPU (Whisper would be too slow) and/or no Anthropic API key of its
/// own (the server holds one key, shared across whichever clients it's configured to
/// trust). Same three operations as LocalInferenceBackend, over HTTP instead of in-process.
///
/// A random per-instance [sessionId] is sent with every request so the server can keep a
/// separate Claude conversation history per client (see Clicky.Server) instead of every
/// connected client's turns bleeding into one shared context -- the same per-mode isolation
/// ClaudeService already does locally (see its _histories dictionary), just keyed by
/// session on top of by mode.
/// </summary>
public class RemoteInferenceBackend : IInferenceBackend
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    public RemoteInferenceBackend(string baseUrl, string authToken)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _http = new HttpClient();
        if (!string.IsNullOrWhiteSpace(authToken))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
    }

    public async Task<string> TranscribeAsync(byte[] pcm16Mono16k, CancellationToken ct = default)
    {
        using var content = new ByteArrayContent(pcm16Mono16k);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

        HttpResponseMessage response;
        try
        {
            response = await _http.PostAsync($"{_baseUrl}/transcribe", content, ct);
        }
        catch (Exception ex)
        {
            throw new Exception($"Clicky server unreachable at {_baseUrl}: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Clicky server /transcribe error {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        return json?["transcript"]?.GetValue<string>() ?? "";
    }

    public async IAsyncEnumerable<string> StreamResponseAsync(
        string transcript, List<ScreenshotResult> screenshots, InteractionMode mode,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["sessionId"] = _sessionId,
            ["transcript"] = transcript,
            ["mode"] = mode.ToString(),
            ["screenshots"] = new JsonArray(screenshots.Select(s => (JsonNode)new JsonObject
            {
                ["base64"] = s.Base64,
                ["label"] = s.Label,
                ["boundsX"] = s.Bounds.X,
                ["boundsY"] = s.Bounds.Y,
                ["boundsWidth"] = s.Bounds.Width,
                ["boundsHeight"] = s.Bounds.Height,
            }).ToArray()),
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/respond")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            throw new Exception($"Clicky server unreachable at {_baseUrl}: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
            throw new Exception($"Clicky server /respond error {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync(ct)}");

        // The server streams back plain UTF-8 text chunks (Claude's own text deltas,
        // relayed as-is) -- no JSON/SSE framing, just read whatever's arrived so far.
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);
        var buffer = new char[512];

        while (!ct.IsCancellationRequested)
        {
            int read = await reader.ReadAsync(buffer, ct);
            if (read == 0) break;
            yield return new string(buffer, 0, read);
        }
    }

    public async Task<(int physX, int physY)> DetectElementAsync(
        string base64Jpeg, string elementDescription, int screenWidth, int screenHeight,
        CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["base64Jpeg"] = base64Jpeg,
            ["elementDescription"] = elementDescription,
            ["screenWidth"] = screenWidth,
            ["screenHeight"] = screenHeight,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/detect-element")
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            throw new Exception($"Clicky server unreachable at {_baseUrl}: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
            return (-1, -1);

        var json = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
        int physX = json?["physX"]?.GetValue<int>() ?? -1;
        int physY = json?["physY"]?.GetValue<int>() ?? -1;
        return (physX, physY);
    }

    public ValueTask DisposeAsync()
    {
        _http.Dispose();
        return ValueTask.CompletedTask;
    }
}
