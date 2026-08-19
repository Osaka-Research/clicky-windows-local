using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using ClickyWindows.Helpers;
using ClickyWindows.Models;
using ClickyWindows.Settings;

namespace ClickyWindows.Services;

public record ClaudeResponse(string Text, List<PointTarget> Points);
public record PointTarget(int X, int Y, string Label);

/// <summary>
/// Streams responses from Claude via Server-Sent Events. Parses [POINT:x,y:label] tags
/// from the response for overlay positioning. Maintains conversation history for context
/// across turns.
/// </summary>
public partial class ClaudeService
{
    private static readonly HttpClient Http = new();

    // Match [POINT:123,456:element_name] tags in Claude's response
    [GeneratedRegex(@"\[POINT:(\d+),(\d+):([^\]]*)\]")]
    private static partial Regex PointTagRegex();

    private const string CommonPromptTail = """
        Respond with the ready-to-speak answer itself — first person, as if you are the
        candidate answering out loud right now — never as advice about how to answer, and
        never prefaced with things like "Sure, here's how you could answer" or "You could
        say...". Just say it, the way the candidate would actually say it sitting across
        from the interviewer. No labels, no headers, no "Answer:" prefix — the very first
        word is the first word they'd speak.

        Keep it tight enough to actually speak out loud in real time — the interviewer is
        waiting live, not reading a document. Aim for roughly 20–45 seconds of natural
        speech (about 60–130 words) for most questions. Only run longer if the question
        explicitly calls for a deep technical walkthrough that would lose its substance if
        compressed further.

        Never write code, code blocks, triple backticks, markdown, bullet points, numbered
        lists, or headers — no exceptions, even for "how does X work" or "walk me through
        your approach to Y" questions. Say it the way you'd actually explain it out loud
        with no slides or screen, one idea flowing into the next, structured with words
        like "first... then... and that's when" instead of a written list.

        Sound like a real person thinking on their feet, not a rehearsed essay: contractions,
        natural phrasing, an occasional "so" or "basically" or "the way I'd put it is" —
        confident and warm, structured enough to follow, never stiff or corporate. If the
        question invites a personal example and none was given in context, answer with a
        plausible, specific-sounding one rather than a vague generality — a real interviewer
        can tell the difference, and a script with no concrete detail is useless to read
        verbatim. Same language the question was asked in.

        Never restate, repeat, paraphrase, or acknowledge the question before answering it —
        no "So you're asking about X", no "Great question", no repeating back what was
        transcribed or what's on screen. Launch straight into the answer itself, the first
        word out of your mouth being the first word of the actual response.
        """;

    private const string WithScreenPrompt = """
        You are Clicky, a live interview co-pilot. The user is in an interview (real or a
        rehearsal) and shares whatever's on their screen — a video call showing the
        interviewer's question, a coding platform, a job description or resume they're
        being asked about. Read whatever question, prompt, or coding problem is visible and
        hand back the exact words the user should say next.

        POINT tags — use ONLY when the user explicitly asks you to locate, show, find, or point to
        a specific element on screen (e.g. "where is the save button?", "point to the menu").
        Do NOT include POINT tags for general questions, greetings, or answers that don't require
        highlighting a specific location.

        When a POINT tag IS appropriate, format it as [POINT:x,y:label] where x,y are the screen
        coordinates in pixels of the element center, and label is a short description (2-4 words).

        """ + CommonPromptTail;

    // Used when no screenshot was captured this turn (Answer mode). Without this, a weaker
    // model can latch onto "can see the user's screen" from a reused/similar system prompt
    // and hallucinate a nonexistent image rather than just answering as a normal assistant.
    private const string NoScreenPrompt = """
        You are Clicky, a live interview co-pilot. For this specific message, no screenshot
        was captured or sent — you have no visual access to the user's screen, or to any
        image, document, or anything visual, right now; you're working only from the
        spoken question. Never claim or imply you can see something; if the question
        genuinely requires seeing the screen to answer (e.g. it references a diagram or
        code that was on screen), say so plainly and ask the user to ask again in a mode
        that shares the screen, instead of guessing or inventing details about what might
        be there.

        """ + CommonPromptTail;

    private readonly AppSettings _settings;

    // Each hotkey/mode gets its own conversation history -- an Action-mode question and
    // an unrelated Answer-mode question a minute later must never end up in the same
    // Claude context, or answers start referencing/repeating the wrong turn.
    private readonly Dictionary<InteractionMode, ConversationHistory> _histories = new();

    private ConversationHistory HistoryFor(InteractionMode mode)
    {
        if (!_histories.TryGetValue(mode, out var history))
        {
            history = new ConversationHistory(maxTurns: 10);
            _histories[mode] = history;
        }
        return history;
    }

    public ClaudeService(AppSettings settings)
    {
        _settings = settings;

        // Pre-configure headers
        Http.DefaultRequestHeaders.Remove("x-api-key");
        if (!string.IsNullOrWhiteSpace(settings.AnthropicApiKey))
            Http.DefaultRequestHeaders.Add("x-api-key", settings.AnthropicApiKey);
        Http.DefaultRequestHeaders.Remove("anthropic-version");
        Http.DefaultRequestHeaders.Add("anthropic-version", "2023-06-01");
    }

    /// <summary>
    /// Sends transcript + screenshots to Claude and streams back the response.
    /// Yields text chunks as they arrive.
    /// </summary>
    public async IAsyncEnumerable<string> StreamResponseAsync(
        string transcript,
        List<Services.ScreenshotResult> screenshots,
        InteractionMode mode,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var history = HistoryFor(mode);

        // Build the content array: images first, then text
        var contentArray = new JsonArray();

        foreach (var shot in screenshots)
        {
            contentArray.Add(new JsonObject
            {
                ["type"] = "image",
                ["source"] = new JsonObject
                {
                    ["type"] = "base64",
                    ["media_type"] = "image/jpeg",
                    ["data"] = shot.Base64,
                },
            });
            contentArray.Add(new JsonObject
            {
                ["type"] = "text",
                ["text"] = shot.Label,
            });
        }

        contentArray.Add(new JsonObject
        {
            ["type"] = "text",
            ["text"] = transcript,
        });

        // Build messages array with conversation history
        var messages = new JsonArray();
        foreach (var turn in history.Turns)
        {
            messages.Add(new JsonObject
            {
                ["role"] = turn.Role,
                ["content"] = turn.Content,
            });
        }
        // Add current user message (text only for history, images are in current message)
        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = contentArray,
        });

        var body = new JsonObject
        {
            ["model"] = _settings.ClaudeModel,
            ["max_tokens"] = 1024,
            ["stream"] = true,
            ["system"] = screenshots.Count > 0 ? WithScreenPrompt : NoScreenPrompt,
            ["messages"] = messages,
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _settings.ClaudeApiUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex)
        {
            throw new Exception($"Claude API request failed: {ex.Message}", ex);
        }

        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(ct);
            throw new Exception($"Claude API error {(int)response.StatusCode}: {errorBody}");
        }

        var fullResponse = new StringBuilder();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new System.IO.StreamReader(stream);

        while (!reader.EndOfStream && !ct.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(ct);
            if (line == null) break;

            if (!line.StartsWith("data: ")) continue;
            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            string? chunk = null;
            try
            {
                var node = JsonNode.Parse(data);
                if (node?["type"]?.GetValue<string>() == "content_block_delta")
                    chunk = node["delta"]?["text"]?.GetValue<string>();
            }
            catch { continue; }

            if (!string.IsNullOrEmpty(chunk))
            {
                fullResponse.Append(chunk);
                yield return chunk;
            }
        }

        // Save to conversation history (strip POINT tags for cleaner history)
        var responseText = fullResponse.ToString();
        var cleanText = PointTagRegex().Replace(responseText, "").Trim();
        history.AddUserMessage(transcript);
        history.AddAssistantMessage(cleanText);
    }

    /// <summary>
    /// Parses [POINT:x,y:label] tags from a complete response string.
    /// </summary>
    public static List<PointTarget> ParsePoints(string responseText)
    {
        var points = new List<PointTarget>();
        foreach (Match m in PointTagRegex().Matches(responseText))
        {
            if (int.TryParse(m.Groups[1].Value, out int x) &&
                int.TryParse(m.Groups[2].Value, out int y))
            {
                points.Add(new PointTarget(x, y, m.Groups[3].Value));
            }
        }
        return points;
    }

    /// <summary>
    /// Strips [POINT:...] tags from text before sending to TTS.
    /// </summary>
    public static string StripPointTags(string text) =>
        PointTagRegex().Replace(text, "").Trim();

    /// <summary>
    /// Uses Claude's Computer Use API to precisely locate a named UI element on screen.
    /// Sends a resized screenshot (at CU resolution) and gets back physical pixel coordinates.
    /// Returns (-1, -1) if the element cannot be found.
    /// </summary>
    public async Task<(int physX, int physY)> DetectElementAsync(
        string base64Jpeg,
        string elementDescription,
        int screenWidth, int screenHeight,
        CancellationToken ct = default)
    {
        var (cuW, cuH) = CoordinateHelper.DetectComputerUseResolution(screenWidth, screenHeight);

        var body = new JsonObject
        {
            ["model"] = _settings.ClaudeModel,
            ["max_tokens"] = 256,
            ["tools"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "computer_20251124",
                    ["name"] = "computer",
                    ["display_width_px"] = cuW,
                    ["display_height_px"] = cuH,
                },
            },
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["type"] = "image",
                            ["source"] = new JsonObject
                            {
                                ["type"] = "base64",
                                ["media_type"] = "image/jpeg",
                                ["data"] = base64Jpeg,
                            },
                        },
                        new JsonObject
                        {
                            ["type"] = "text",
                            ["text"] = $"Locate the '{elementDescription}' element on this screen and move the mouse to it.",
                        },
                    },
                },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, _settings.ClaudeApiUrl)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("anthropic-beta", "computer-use-2025-11-24");

        HttpResponseMessage response;
        try
        {
            response = await Http.SendAsync(request, ct);
        }
        catch (Exception ex)
        {
            throw new Exception($"Computer Use API request failed: {ex.Message}", ex);
        }

        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new Exception($"Computer Use API error {(int)response.StatusCode}: {json}");

        var node = JsonNode.Parse(json);
        var content = node?["content"]?.AsArray();
        if (content != null)
        {
            foreach (var block in content)
            {
                if (block?["type"]?.GetValue<string>() == "tool_use" &&
                    block?["name"]?.GetValue<string>() == "computer")
                {
                    var coord = block?["input"]?["coordinate"]?.AsArray();
                    if (coord?.Count >= 2)
                    {
                        int cuX = coord[0]!.GetValue<int>();
                        int cuY = coord[1]!.GetValue<int>();
                        // Scale from CU space to physical pixels
                        int physX = (int)((double)cuX / cuW * screenWidth);
                        int physY = (int)((double)cuY / cuH * screenHeight);
                        return (physX, physY);
                    }
                }
            }
        }

        return (-1, -1);
    }
}
