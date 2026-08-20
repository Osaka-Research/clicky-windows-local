using System.Collections.Concurrent;
using System.Drawing;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Services;
using ClickyWindows.Settings;

// Separate app-data folder from every client -- its own settings.json (API key, model,
// Whisper size, auth token), its own log file, never confused with a Windows/Mac install
// running on the same machine for local testing.
AppPaths.AppFolderName = "AutoServer";

var settings = AppSettings.Load();
if (string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
{
    settings.Save(); // write a template so there's something to edit
    Console.WriteLine("No AnthropicApiKey configured yet. Edit this file and restart:");
    Console.WriteLine($"  {Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AutoServer", "settings.json")}");
    return;
}
if (string.IsNullOrWhiteSpace(settings.ServerAuthToken))
{
    Logger.Error("[Server] No ServerAuthToken set in settings.json -- anyone who can reach " +
                 "this server can use your Anthropic key for free. Set one and restart before " +
                 "exposing this beyond localhost.");
}

Logger.Log("=== Auto Server starting ===");
Logger.Log($"Whisper model: {settings.WhisperModelSize}, Claude model: {settings.ClaudeModel}");

var whisper = new LocalWhisperService(settings.WhisperModelSize);
_ = whisper.EnsureModelLoadedAsync();

// One ClaudeService per connected client (sessionId), so each client's per-mode
// conversation history (see ClaudeService._histories) stays its own -- otherwise every
// client hitting this server would share one Claude context and answers would start
// referencing/repeating other people's turns, the exact per-mode bug fixed locally
// earlier for a single client, now at the client-isolation level instead.
var sessions = new ConcurrentDictionary<string, ClaudeService>();

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.Use(async (ctx, next) =>
{
    if (!string.IsNullOrWhiteSpace(settings.ServerAuthToken))
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (auth != $"Bearer {settings.ServerAuthToken}")
        {
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsync("unauthorized");
            return;
        }
    }
    await next();
});

app.MapGet("/", () => "Auto Server is running.");

app.MapPost("/transcribe", async (HttpContext ctx) =>
{
    using var ms = new MemoryStream();
    await ctx.Request.Body.CopyToAsync(ms, ctx.RequestAborted);
    var pcm16 = ms.ToArray();

    Logger.Log($"[Server] /transcribe: {pcm16.Length} bytes");
    var transcript = await whisper.TranscribeAsync(pcm16, ctx.RequestAborted);
    Logger.Info($"Heard: \"{transcript}\"");

    return Results.Json(new { transcript });
});

app.MapPost("/respond", async (HttpContext ctx, RespondRequest req) =>
{
    var claude = sessions.GetOrAdd(req.SessionId, _ => new ClaudeService(settings));
    var mode = Enum.TryParse<InteractionMode>(req.Mode, out var m) ? m : InteractionMode.Action;
    var screenshots = (req.Screenshots ?? []).Select(s => new ScreenshotResult(
        Convert.FromBase64String(s.Base64), s.Base64, s.Label,
        new Rectangle(s.BoundsX, s.BoundsY, s.BoundsWidth, s.BoundsHeight))).ToList();

    Logger.Log($"[Server] /respond session={req.SessionId} mode={mode} " +
               $"screenshots={screenshots.Count}: \"{req.Transcript}\"");

    // Plain streamed text, not JSON/SSE -- the client reads it back the same way
    // (RemoteInferenceBackend.StreamResponseAsync), no framing protocol needed on either
    // side. X-Accel-Buffering tells nginx (if this is ever put behind one) not to buffer
    // the whole response before forwarding it, which would defeat the streaming entirely.
    ctx.Response.ContentType = "text/plain; charset=utf-8";
    ctx.Response.Headers.Append("X-Accel-Buffering", "no");

    try
    {
        await foreach (var chunk in claude.StreamResponseAsync(req.Transcript, screenshots, mode, ctx.RequestAborted))
        {
            await ctx.Response.WriteAsync(chunk, ctx.RequestAborted);
            await ctx.Response.Body.FlushAsync(ctx.RequestAborted);
        }
    }
    catch (Exception ex)
    {
        Logger.Error($"[Server] /respond error: {ex.Message}");
    }
});

app.MapPost("/detect-element", async (DetectElementRequest req) =>
{
    // Computer Use calls are one-shot/stateless (no conversation history involved), so
    // there's no session to key this by -- a throwaway ClaudeService is fine here.
    var claude = new ClaudeService(settings);
    var (physX, physY) = await claude.DetectElementAsync(
        req.Base64Jpeg, req.ElementDescription, req.ScreenWidth, req.ScreenHeight);
    return Results.Json(new { physX, physY });
});

Logger.Log($"Log file: {Logger.LogFilePath}");
app.Run();

record RespondRequest(string SessionId, string Transcript, string Mode, List<ScreenshotDto>? Screenshots);
record ScreenshotDto(string Base64, string Label, int BoundsX, int BoundsY, int BoundsWidth, int BoundsHeight);
record DetectElementRequest(string Base64Jpeg, string ElementDescription, int ScreenWidth, int ScreenHeight);
