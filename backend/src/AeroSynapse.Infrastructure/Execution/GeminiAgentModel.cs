using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Management;
using Microsoft.Extensions.Options;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class AgentOptions
{
    /// <summary>"gemini" (Google API, needs ApiKey) or "ollama" (a local model at Endpoint, no key, no credits).</summary>
    public string Provider { get; set; } = "gemini";
    public string ApiKey { get; set; } = "";
    public string Model { get; set; } = "gemini-3.8-flash";
    public string Endpoint { get; set; } = "https://generativelanguage.googleapis.com/v1beta";
    public int TimeoutSeconds { get; set; } = 10;
    public int MaxModelCallsPerRun { get; set; } = 100;
    public int DailyCallLimit { get; set; } = 2000;
}

/// <summary>
/// Gemini, or a local Ollama model, picks one of the offered option ids under a JSON schema; it cannot add actions or numbers.
/// </summary>
public sealed class GeminiAgentModel(HttpClient http, IOptions<AgentOptions> options) : IAgentModel
{
    // Diacritics stay readable for the model ("ă", not "\u0103"); the text is escaped again as a JSON string in the request.
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    public AgentStatus Status => new(Configured(options.Value), options.Value.Model);

    public async Task<AgentChoice> ChooseAsync(AgentPrompt prompt, CancellationToken token)
    {
        var settings = options.Value;
        var ids = prompt.Options.Select(option => option.Id).ToArray();
        var user = JsonSerializer.Serialize(new
        {
            minute = prompt.Minute, clock = prompt.Clock, state = prompt.State,
            options = prompt.Options.Select(option => new { id = option.Id, action = option.Text })
        }, Web);
        var schema = new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject
            {
                ["optionId"] = new JsonObject { ["type"] = "STRING", ["enum"] = Enum(ids) },
                ["reason"] = new JsonObject { ["type"] = "STRING" }
            },
            ["required"] = new JsonArray("optionId", "reason")
        };
        // A local model may first load into memory, so it gets a little longer than one Gemini round trip.
        var (status, answer, input, output) = await GenerateAsync(AgentPrompts.System(prompt.TransferMinutes), user, schema, 256,
            Local(settings) ? Math.Max(settings.TimeoutSeconds, 20) : settings.TimeoutSeconds, token);
        if (answer is not { } value) return new(status, null, null, settings.Model);
        try
        {
            var id = value.GetProperty("optionId").GetString();
            var reason = Clean(value.GetProperty("reason").GetString(), 300);
            if (id is null || !ids.Contains(id, StringComparer.Ordinal)) return new("invalid_response", null, null, settings.Model, input, output);
            return new("chosen", id, reason, settings.Model, input, output);
        }
        catch (Exception failure) when (failure is KeyNotFoundException or InvalidOperationException)
        { return new("invalid_response", null, null, settings.Model); }
    }

    public async Task<OptimizationAdvice> AdviseAsync(object facts, IReadOnlyList<string> optionIds, CancellationToken token)
    {
        var settings = options.Value;
        // Only words: the recommendation, titles and numbers are decided in code, which also checks these sentences.
        var schema = new JsonObject
        {
            ["type"] = "OBJECT",
            ["properties"] = new JsonObject
            {
                ["options"] = new JsonObject
                {
                    ["type"] = "ARRAY",
                    ["items"] = new JsonObject
                    {
                        ["type"] = "OBJECT",
                        ["properties"] = new JsonObject
                        {
                            ["id"] = new JsonObject { ["type"] = "STRING", ["enum"] = Enum(optionIds) },
                            ["explanation"] = new JsonObject { ["type"] = "STRING" }
                        },
                        ["required"] = new JsonArray("id", "explanation")
                    }
                },
                ["advice"] = new JsonObject { ["type"] = "STRING" }
            },
            ["required"] = new JsonArray("options", "advice")
        };
        // Several options to write about: a longer budget than one live-agent pick, longest for a model running on this machine.
        var (status, answer, _, _) = await GenerateAsync(OptimizationPrompts.System, JsonSerializer.Serialize(facts, Web), schema, 2048,
            Math.Max(settings.TimeoutSeconds, Local(settings) ? 90 : 25), token);
        if (answer is not { } value) return new(status, settings.Model);
        try
        {
            var written = value.GetProperty("options").EnumerateArray()
                .Select(o => new OptimizationAdviceOption(o.GetProperty("id").GetString() ?? "", Clean(o.GetProperty("explanation").GetString(), 300)))
                .Where(o => optionIds.Contains(o.Id, StringComparer.Ordinal) && o.Explanation.Length > 0).ToArray();
            var advice = Clean(value.GetProperty("advice").GetString(), 400);
            if (written.Length == 0 && advice.Length == 0) return new("invalid_response", settings.Model);
            return new("chosen", settings.Model, advice, written);
        }
        catch (Exception failure) when (failure is KeyNotFoundException or InvalidOperationException)
        { return new("invalid_response", settings.Model); }
    }

    /// <summary>One schema-constrained model call; the answer is null, with the reason in Status, when there is none.</summary>
    private async Task<(string Status, JsonElement? Answer, long? Input, long? Output)> GenerateAsync(string system, string user, JsonObject schema,
        int maxTokens, int timeoutSeconds, CancellationToken token)
    {
        var settings = options.Value;
        if (!Configured(settings)) return ("unconfigured", null, null, null);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        if (Local(settings)) return await OllamaAsync(settings, system, user, schema, maxTokens, budget.Token, token);
        var body = new JsonObject
        {
            ["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = system }) },
            ["contents"] = new JsonArray(new JsonObject
            {
                ["role"] = "user",
                ["parts"] = new JsonArray(new JsonObject { ["text"] = user })
            }),
            ["generationConfig"] = new JsonObject
            {
                // Gemini 3+ cannot switch thinking off and rejects the 2.x budget field; its thinking tokens share maxOutputTokens.
                ["temperature"] = 0, ["maxOutputTokens"] = Legacy(settings.Model) ? maxTokens : maxTokens + 2048, ["responseMimeType"] = "application/json",
                ["thinkingConfig"] = Legacy(settings.Model) ? new JsonObject { ["thinkingBudget"] = 0 } : new JsonObject { ["thinkingLevel"] = "low" },
                ["responseSchema"] = schema
            }
        };
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{settings.Endpoint.TrimEnd('/')}/models/{Uri.EscapeDataString(settings.Model)}:generateContent")
            { Content = JsonContent.Create(body) };
            request.Headers.Add("x-goog-api-key", settings.ApiKey);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            // 402/429: the Gemini project has no credit left or hit its rate limit.
            if ((int)response.StatusCode is 402 or 429) return ("quota_exhausted", null, null, null);
            if (!response.IsSuccessStatusCode) return ("unavailable", null, null, null);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(budget.Token), cancellationToken: budget.Token);
            var root = document.RootElement;
            long? Count(string name) => root.TryGetProperty("usageMetadata", out var usage) && usage.TryGetProperty(name, out var value) ? value.GetInt64() : null;
            var text = string.Concat(root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts").EnumerateArray()
                .Select(part => part.TryGetProperty("text", out var value) ? value.GetString() : ""));
            using var answer = JsonDocument.Parse(text);
            return ("answered", answer.RootElement.Clone(), Count("promptTokenCount"), Count("candidatesTokenCount"));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ("timeout", null, null, null); }
        catch (HttpRequestException) { return ("unavailable", null, null, null); }
        catch (Exception failure) when (failure is JsonException or KeyNotFoundException
            or InvalidOperationException or IndexOutOfRangeException or FormatException)
        { return ("invalid_response", null, null, null); }
    }

    /// <summary>The same call to Ollama's chat API: the schema goes in "format", the answer comes back as the message text.</summary>
    private async Task<(string Status, JsonElement? Answer, long? Input, long? Output)> OllamaAsync(AgentOptions settings, string system, string user,
        JsonObject schema, int maxTokens, CancellationToken budget, CancellationToken token)
    {
        var body = new JsonObject
        {
            ["model"] = settings.Model, ["stream"] = false, ["format"] = JsonSchema(schema),
            ["messages"] = new JsonArray(
                new JsonObject { ["role"] = "system", ["content"] = system },
                new JsonObject { ["role"] = "user", ["content"] = user }),
            ["options"] = new JsonObject { ["temperature"] = 0, ["num_predict"] = maxTokens }
        };
        try
        {
            using var response = await http.PostAsJsonAsync($"{settings.Endpoint.TrimEnd('/')}/api/chat", body, budget);
            // 404: the model was never pulled into Ollama.
            if (!response.IsSuccessStatusCode) return ("unavailable", null, null, null);
            using var document = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(budget), cancellationToken: budget);
            var root = document.RootElement;
            long? Count(string name) => root.TryGetProperty(name, out var value) && value.TryGetInt64(out var count) ? count : null;
            using var answer = JsonDocument.Parse(root.GetProperty("message").GetProperty("content").GetString() ?? "");
            return ("answered", answer.RootElement.Clone(), Count("prompt_eval_count"), Count("eval_count"));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return ("timeout", null, null, null); }
        catch (HttpRequestException) { return ("unavailable", null, null, null); }
        catch (Exception failure) when (failure is JsonException or KeyNotFoundException or InvalidOperationException)
        { return ("invalid_response", null, null, null); }
    }

    /// <summary>Gemini's schema dialect spells types in capitals ("OBJECT"); standard JSON Schema, which Ollama reads, does not.</summary>
    private static JsonNode JsonSchema(JsonNode node) => node switch
    {
        JsonObject o => new JsonObject(o.Select(p => KeyValuePair.Create(p.Key,
            p.Key == "type" && p.Value is JsonValue v && v.TryGetValue<string>(out var type) ? (JsonNode?)type.ToLowerInvariant() : p.Value is null ? null : JsonSchema(p.Value)))),
        JsonArray a => new JsonArray(a.Select(item => item is null ? null : JsonSchema(item)).ToArray()),
        _ => node.DeepClone()
    };

    private static bool Local(AgentOptions settings) => settings.Provider.Equals("ollama", StringComparison.OrdinalIgnoreCase);

    private static bool Legacy(string model) => model.StartsWith("gemini-2", StringComparison.OrdinalIgnoreCase);

    private static JsonArray Enum(IEnumerable<string> ids) => new(ids.Select(id => (JsonNode)id!).ToArray());

    // Gemini needs its key and HTTPS; a local Ollama needs neither, only where it listens.
    private static bool Configured(AgentOptions settings) => !string.IsNullOrWhiteSpace(settings.Model) && settings.TimeoutSeconds is >= 1 and <= 30
        && Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var uri)
        && (Local(settings) ? uri.Scheme is "http" or "https" : uri.Scheme == "https" && !string.IsNullOrWhiteSpace(settings.ApiKey));

    // Model text is shown to operators: one line, printable, bounded.
    private static string Clean(string? value, int limit)
    {
        var text = new string((value ?? "").Where(c => !char.IsControl(c)).ToArray()).Trim();
        return text.Length <= limit ? text : text[..limit];
    }
}
