using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Playwright;
using GeminiBridge;

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

var chatService = new ChatService();
string profilePath = "PlaywrightProfile";
string fullPath = Path.GetFullPath(profilePath);

if (!Directory.Exists(fullPath)) Directory.CreateDirectory(fullPath);

bool isBrandNew = Directory.GetFileSystemEntries(fullPath).Length == 0;
bool runHeadless = !isBrandNew;

await chatService.InitializePlaywrightAsync(runHeadless);

string loginStatus = await chatService.CheckIfLoggedInAsync(isBrandNew);

if (loginStatus.StartsWith("LoginRequired"))
{
    Console.WriteLine("Login required — complete sign-in in the opened browser window.");
}
else
{
    Console.WriteLine("Authentication successful.");
}


app.MapGet("/healthz", () => Results.Json(new { authenticated = true }));

app.MapGet("/v1/models", () => Results.Json(new 
{
    data = new[] { new { id = "gemini-web", @object = "model", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), owned_by = "google" } }
}));

var stateFile = Path.Combine(fullPath, "gemini_state.json");
SessionState sessionState = new SessionState();
if (File.Exists(stateFile))
{
    try { sessionState = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(stateFile)); } catch { }
}

app.MapGet("/v1/debug/history", async () =>
{
    try
    {
        if (!string.IsNullOrEmpty(sessionState.ChatUrl) && !chatService._page.Url.StartsWith(sessionState.ChatUrl))
        {
            await chatService._page.GotoAsync(sessionState.ChatUrl);
            await chatService._page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.DOMContentLoaded);
            await Task.Delay(2000);
        }
        string history = await chatService.GetChatHistoryMessagesAsync("gemini");
        return Results.Text(history);
    }
    catch (Exception ex)
    {
        return Results.Text("Error: " + ex.ToString());
    }
});

app.MapGet("/v1/debug/screenshot", async () =>
{
    try
    {
        byte[] screenshot = await chatService._page.ScreenshotAsync(new Microsoft.Playwright.PageScreenshotOptions { FullPage = true });
        return Results.File(screenshot, "image/png");
    }
    catch (Exception ex)
    {
        return Results.Text("Error: " + ex.ToString());
    }
});

string GetHash(string input)
{
    using (SHA256 sha = SHA256.Create())
    {
        byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(input));
        StringBuilder sb = new StringBuilder();
        foreach (byte b in bytes) sb.Append(b.ToString("x2"));
        return sb.ToString();
    }
}

string GetContentString(JsonElement content)
{
    if (content.ValueKind == JsonValueKind.String)
        return content.GetString() ?? "";
    if (content.ValueKind == JsonValueKind.Array)
    {
        var sb = new StringBuilder();
        foreach (var part in content.EnumerateArray())
        {
            if (part.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "text")
            {
                if (part.TryGetProperty("text", out var textProp))
                    sb.Append(textProp.GetString());
            }
        }
        return sb.ToString();
    }
    return "";
}

string FixUnquotedJsonKeys(string json)
{
    return Regex.Replace(json, @"([{,]\s*)(\w+)\s*:", "$1\"$2\":");
}

var chatSemaphore = new SemaphoreSlim(1, 1);

app.MapPost("/v1/chat/completions", async (HttpContext context) => 
{
    await chatSemaphore.WaitAsync();
    try
    {
        var request = await JsonSerializer.DeserializeAsync<OpenAiChatRequest>(context.Request.Body);
        if (request == null || request.Messages == null || !request.Messages.Any())
            return Results.BadRequest("Invalid request body");

        var incomingMessages = request.Messages.ToList();
        string sysMsg = "";
        var sysNode = incomingMessages.FirstOrDefault(m => m.Role == "system");
        if (sysNode != null)
        {
            sysMsg = GetContentString(sysNode.Content);
            sysMsg = Regex.Replace(sysMsg, @"\[tool_call:[^\]]+\]", "");
            sysMsg = Regex.Replace(sysMsg, @"<example>[\s\S]*?</example>", "");
            incomingMessages.Remove(sysNode);
        }

        string toolsHash = request.Tools.HasValue ? GetHash(request.Tools.Value.ToString()) : "";

        bool isContinuation = false;
        int newStartIndex = 0;

        if (sessionState.LastMessages != null && incomingMessages.Count >= sessionState.LastMessages.Count)
        {
            bool match = true;
            for (int i = 0; i < sessionState.LastMessages.Count; i++)
            {
                var oldMsg = sessionState.LastMessages[i];
                var newMsg = incomingMessages[i];
                if (oldMsg.Role != newMsg.Role || GetContentString(oldMsg.Content) != GetContentString(newMsg.Content))
                {
                    match = false;
                    break;
                }
            }
            if (match)
            {
                isContinuation = true;
                newStartIndex = sessionState.LastMessages.Count;
            }
        }

        if (!isContinuation || toolsHash != sessionState.ToolsHash)
        {
            isContinuation = false;
            newStartIndex = 0;
        }

        var sb = new StringBuilder();
        for (int i = newStartIndex; i < incomingMessages.Count; i++)
        {
            var msg = incomingMessages[i];
            string content = GetContentString(msg.Content);
            if (msg.Role == "user")
                sb.AppendLine(content);
            else if (msg.Role == "assistant")
            {
                sb.AppendLine($"[Your previous reply]: {content}");
                if (msg.ToolCalls.HasValue && msg.ToolCalls.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var call in msg.ToolCalls.Value.EnumerateArray())
                    {
                        if (call.TryGetProperty("function", out var func))
                        {
                            string name = func.TryGetProperty("name", out var n) ? n.GetString() : "";
                            string args = func.TryGetProperty("arguments", out var a) ? a.GetString() : "";
                            sb.AppendLine($"[You called tool '{name}' with args: {args}]");
                        }
                    }
                }
            }
            else if (msg.Role == "tool")
            {
                string tname = msg.Name ?? "unknown_tool";
                sb.AppendLine($"TOOL RESULT for {tname}:\n{content}");
            }
        }
        string textToSend = sb.ToString().Trim();

        int promptTokens = textToSend.Length / 4;
        
        string responseText = "";

        if (!isContinuation)
        {
            string toolsText = request.Tools.HasValue ? request.Tools.Value.ToString() : "None";
            string combinedText = $"<system_instructions>\n{sysMsg}\n</system_instructions>\n\n<tools_available>\n{toolsText}\n</tools_available>\n\n<instructions_for_tool_calls>\nYou must use exactly this format to call a tool on its own line:\nTOOLCALL: {{\"name\": \"...\", \"arguments\": {{...}}}}\nDo not use any other format or markdown for tool calls.\n</instructions_for_tool_calls>\n\n<user_request>\n{textToSend}\n</user_request>\n\nFulfill the user_request immediately. If you need to use a tool, output the TOOLCALL on its own line. Do not output any conversational greetings, introductions, or explanations. Start your response directly with the tool call or the answer.";
            
            responseText = await chatService.SendMessageAsync(combinedText, false, "gemini", false);
            sessionState.ToolsHash = toolsHash;
            sessionState.CumulativePromptTokens = combinedText.Length / 4;
        }
        else
        {
            bool keepSession = true;
            if (!string.IsNullOrEmpty(sessionState.ChatUrl) && (chatService._page.Url == null || !chatService._page.Url.StartsWith(sessionState.ChatUrl)))
            {
                try
                {
                    await chatService._page.GotoAsync(sessionState.ChatUrl);
                    await chatService._page.WaitForLoadStateAsync(Microsoft.Playwright.LoadState.DOMContentLoaded);
                }
                catch { keepSession = false; }
            }
            if (!string.IsNullOrEmpty(textToSend))
                responseText = await chatService.SendMessageAsync(textToSend, false, "gemini", keepSession);
        }

        sessionState.CumulativePromptTokens += promptTokens;

        if (responseText.StartsWith("LoginRequired"))
            return Results.Json(new { error = "Browser not authenticated. Please log in first." }, statusCode: 503);
        if (responseText.StartsWith("Error: Gemini did not respond"))
            return Results.StatusCode(504);
        if (responseText.StartsWith("Error"))
            return Results.StatusCode(500);

        var toolCallsObj = new List<ToolCallObj>();
        string cleanResponseText = responseText;

        int retries = 0;
        while (retries < 2 && !string.IsNullOrEmpty(responseText))
        {
            toolCallsObj.Clear();
            var cleanLines = new List<string>();
            bool malformed = false;
            var lines = responseText.Split('\n');
            foreach (var line in lines)
            {
                string t = line.Trim();
                if (t.StartsWith("TOOLCALL:"))
                {
                    string jsonStr = t.Substring("TOOLCALL:".Length).Trim();
                    try
                    {
                        var callObj = JsonSerializer.Deserialize<JsonElement>(jsonStr);
                        if (callObj.TryGetProperty("name", out var n) && callObj.TryGetProperty("arguments", out var a))
                        {
                            toolCallsObj.Add(new ToolCallObj {
                                id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                                function = new FunctionObj {
                                    name = n.GetString(),
                                    arguments = a.ValueKind == JsonValueKind.String ? a.GetString() : a.ToString()
                                }
                            });
                        }
                        else
                        {
                            malformed = true;
                        }
                    }
                    catch { malformed = true; }
                }
                else if (t.Contains("[tool_call:"))
                {
                    var match = Regex.Match(t, @"\[tool_call:(\w+)\s*(\{.*?\})\]");
                    if (match.Success)
                    {
                        string name = match.Groups[1].Value;
                        string jsonStr = FixUnquotedJsonKeys(match.Groups[2].Value);
                        try
                        {
                            var argsObj = JsonSerializer.Deserialize<JsonElement>(jsonStr);
                            toolCallsObj.Add(new ToolCallObj {
                                id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 8),
                                function = new FunctionObj {
                                    name = name,
                                    arguments = argsObj.ToString()
                                }
                            });
                        }
                        catch { malformed = true; }
                    }
                    else
                    {
                        malformed = true;
                    }
                }
                else
                {
                    cleanLines.Add(line);
                }
            }

            cleanResponseText = string.Join("\n", cleanLines).Trim();

            if (malformed)
            {
                string correction = "TOOL RESULT for system:\nERROR: Invalid or malformed TOOLCALL JSON. Retry in the exact format: TOOLCALL: {\"name\": \"...\", \"arguments\": {...}}";
                responseText = await chatService.SendMessageAsync(correction, false, "gemini", true);
                retries++;
            }
            else
            {
                break;
            }
        }

        sessionState.LastMessages = incomingMessages;
        sessionState.ChatUrl = chatService._page.Url;
        File.WriteAllText(stateFile, JsonSerializer.Serialize(sessionState));

        int completionTokens = cleanResponseText.Length / 4;
        var usageObj = new {
            prompt_tokens = sessionState.CumulativePromptTokens,
            completion_tokens = completionTokens,
            total_tokens = sessionState.CumulativePromptTokens + completionTokens
        };

        var messageObj = new Dictionary<string, object>
        {
            { "role", "assistant" }
        };
        
        if (!string.IsNullOrEmpty(cleanResponseText) || toolCallsObj.Count == 0)
            messageObj["content"] = cleanResponseText;
        else
            messageObj["content"] = null;

        if (toolCallsObj.Count > 0)
            messageObj["tool_calls"] = toolCallsObj;

        if (request.Stream)
        {
            context.Response.ContentType = "text/event-stream";
            string streamId = "chatcmpl-" + Guid.NewGuid().ToString("N");
            
            var chunk1 = new { id = streamId, @object = "chat.completion.chunk", choices = new[] { new { delta = new { role = "assistant" }, index = 0 } } };
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk1)}\n\n");
            await context.Response.Body.FlushAsync();

            if (!string.IsNullOrEmpty(cleanResponseText) || toolCallsObj.Count == 0)
            {
                var chunk2 = new { id = streamId, @object = "chat.completion.chunk", choices = new[] { new { delta = new { content = cleanResponseText }, index = 0 } } };
                await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk2)}\n\n");
                await context.Response.Body.FlushAsync();
            }

            if (toolCallsObj.Count > 0)
            {
                var deltaToolCalls = toolCallsObj.Select((tc, i) => new {
                    index = i,
                    id = tc.id,
                    type = tc.type,
                    function = tc.function
                }).ToArray();

                var chunkTool = new { id = streamId, @object = "chat.completion.chunk", choices = new[] { new { delta = new { tool_calls = deltaToolCalls }, index = 0 } } };
                await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunkTool)}\n\n");
                await context.Response.Body.FlushAsync();
            }

            var chunk3 = new { id = streamId, @object = "chat.completion.chunk", choices = new[] { new { delta = new { }, finish_reason = "stop", index = 0 } }, usage = usageObj };
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(chunk3)}\n\n");
            await context.Response.Body.FlushAsync();

            await context.Response.WriteAsync("data: [DONE]\n\n");
            await context.Response.Body.FlushAsync();
            
            return Results.Empty;
        }
        else
        {
            var response = new
            {
                id = "chatcmpl-" + Guid.NewGuid().ToString("N"),
                @object = "chat.completion",
                created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                model = request.Model ?? "gemini-web",
                choices = new[]
                {
                    new 
                    {
                        index = 0,
                        message = messageObj,
                        finish_reason = "stop"
                    }
                },
                usage = usageObj
            };
            return Results.Json(response);
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine(ex);
        
        if (context.Response.HasStarted)
        {
            var errorChunk = new { id = "chatcmpl-error", @object = "chat.completion.chunk", choices = new[] { new { delta = new { content = $"\n\n[Bridge Error: {ex.Message}]" }, finish_reason = "error", index = 0 } } };
            await context.Response.WriteAsync($"data: {JsonSerializer.Serialize(errorChunk)}\n\n");
            await context.Response.WriteAsync("data: [DONE]\n\n");
            return Results.Empty;
        }
        
        return Results.StatusCode(500);
    }
    finally
    {
        chatSemaphore.Release();
    }
});

app.Run("http://127.0.0.1:8787");

public class SessionState
{
    public List<ChatMessage> LastMessages { get; set; } = new();
    public string ChatUrl { get; set; } = "";
    public string ToolsHash { get; set; } = "";
    public int CumulativePromptTokens { get; set; } = 0;
}

public class OpenAiChatRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; }
    
    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; }
    
    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("tools")]
    public JsonElement? Tools { get; set; }
}

public class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; }
    
    [JsonPropertyName("content")]
    public JsonElement Content { get; set; }
    
    [JsonPropertyName("name")]
    public string Name { get; set; }

    [JsonPropertyName("tool_call_id")]
    public string ToolCallId { get; set; }
    
    [JsonPropertyName("tool_calls")]
    public JsonElement? ToolCalls { get; set; }
}

public class ToolCallObj
{
    public string id { get; set; }
    public string type { get; set; } = "function";
    public FunctionObj function { get; set; }
}

public class FunctionObj
{
    public string name { get; set; }
    public string arguments { get; set; }
}
