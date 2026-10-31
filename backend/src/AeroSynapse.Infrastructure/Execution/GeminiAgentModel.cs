using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Management;
using Microsoft.Extensions.Options;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class AgentOptions
{
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-2.5-flash";
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxModelCallsPerRun { get; set; } = 100;
    public int DailyCallLimit { get; set; } = 2000;
}

/// <summary>Gemini picks one of the offered option ids under a JSON schema; it cannot add actions or numbers.</summary>
public sealed class GeminiAgentModel(HttpClient http, IOptions<AgentOptions> options) : IAgentModel
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public AgentStatus Status => new(Configured(options.Value), options.Value.Model);

    public async Task<AgentChoice> ChooseAsync(AgentPrompt prompt, CancellationToken token)
    {
        var settings = options.Value;
        if (!Configured(settings)) return new("unconfigured", null, null, settings.Model);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        var ids = prompt.Options.Select(option => option.Id).ToArray();
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = AgentPrompts.System(prompt.TransferMinutes) }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = JsonSerializer.Serialize(new
                {
                    minute = prompt.Minute, clock = prompt.Clock, state = prompt.State,
                    options = prompt.Options.Select(option => new { id = option.Id, action = option.Text })
                }, Web) })
            }),
            ["generationConfig"] = new JsonObject
            {
                ["temperature"] = 0, ["maxOutputTokens"] = 256, ["responseMimeType"] = "application/json",
                ["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = 0 },
                ["responseSchema"] = new JsonObject
                {
                    ["type"] = "OBJECT",
                    ["properties"] = new JsonObject
                    {
                        ["optionId"] = new JsonObject { ["type"] = "STRING", ["enum"] = new JsonArray(ids.Select(id => (JsonNode)id!).ToArray()) },
                        ["reason"] = new JsonObject { ["type"] = "STRING" }
                    },
                    ["required"] = new JsonArray("optionId", "reason")
                }
            }
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Endpoint.TrimEnd('/')}/models/{Uri.EscapeDataString(settings.Model)}:generateContent")
            { Content = JsonContent.Create(body) };
            request.Headers.Add("x-goog-api-key", settings.ApiKey);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            // 402/429: the Gemini project has no credit left or hit its rate limit.
            if ((int)response.StatusCode is 402 or 429) return new("quota_exhausted", null, null, settings.Model);
            if (!response.IsSuccessStatusCode) return new("unavailable", null, null, settings.Model);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(budget.Token), cancellationToken: budget.Token);
            var root = document.RootElement;
            long? Count(string name) => root.TryGetProperty("usageMetadata", out var usage) && usage.TryGetProperty(name, out var value) ? value.GetInt64() : null;
            var text = string.Concat(root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray()
                .Select(part => part.TryGetProperty("text", out var value) ? value.GetString() : ""));
            using var answer = JsonDocument.Parse(text);
            var id = answer.RootElement.GetProperty("optionId").GetString();
            var reason = Clean(answer.RootElement.GetProperty("reason").GetString());
            if (id is null || !ids.Contains(id, StringComparer.Ordinal))
                return new("invalid_response", null, null, settings.Model, Count("promptTokenCount"), Count("candidatesTokenCount"));
            return new("chosen", id, reason, settings.Model, Count("promptTokenCount"), Count("candidatesTokenCount"));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("timeout", null, null, settings.Model); }
        catch (Exception failure) when (failure is HttpRequestException or JsonException or KeyNotFoundException
            or InvalidOperationException or IndexOutOfRangeException)
        { return new("invalid_response", null, null, settings.Model); }
    }

    private static bool Configured(AgentOptions settings) => !string.IsNullOrWhiteSpace(settings.ApiKey) && !string.IsNullOrWhiteSpace(settings.Model)
        && settings.TimeoutSeconds is >= 1 and <= 30 && Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri) && uri.Scheme == "https";

    // The reason is shown to operators: one line, printable, bounded.
    private static string Clean(string? value)
    {
        var text = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= 300 ? text : text[..300];
    }
}
