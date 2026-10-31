using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Management;
using Microsoft.Extensions.Options;
using AeroSynapse.Infrastructure.Governance;

namespace AeroSynapse.Infrastructure.Execution;

public sealed class CopilotOptions
{
    public bool Enabled { get; set; }
    public string Endpoint { get; set; } = "http://host.docker.internal:11434/api/chat";
    public string Model { get; set; } = "";
    public string ApiKey { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 8;
}

// The LLM selects a topic. It cannot supply prose, numbers, identifiers, tools or commands.
public sealed class CopilotModelClient(HttpClient http, IOptions<CopilotOptions> options, AiGovernance? governance = null) : ICopilotModel
{
    public async Task<CopilotModelResult> SelectTopicAsync(CopilotModelInput input, CancellationToken token)
    {
        // Disabled/malformed configurations remain local fallbacks without spending quota.
        if (!options.Value.Enabled || !Configured(options.Value))
        {
            var skipped = await Select(input, token);
            if (governance is not null) await governance.RecordAsync(new() { Kind = "llm_skipped", Status = skipped.Status,
                ScenarioId = input.Scope.ScenarioId, RunId = input.Scope.RunId, PolicyVersion = (await governance.PolicyAsync(token)).Version }, token);
            return skipped;
        }
        var lease = governance is null ? null : await governance.ReserveAsync(input.Scope, token);
        if (governance is not null && lease is null) return new("policy_or_budget_denied");
        CopilotModelResult result;
        try { result = await Select(input, token); }
        catch (OperationCanceledException)
        {
            if (lease is not null) await Finish(new("cancelled"));
            throw;
        }
        if (lease is not null) await Finish(result);
        return result;
        async Task Finish(CopilotModelResult value)
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await governance!.FinishAsync(lease!, input.Scope, value, deadline.Token);
        }
    }
    private static bool Configured(CopilotOptions settings) => !string.IsNullOrWhiteSpace(settings.Model) && settings.Model.Length <= 200
        && Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) && endpoint.Scheme is "http" or "https"
        && string.IsNullOrEmpty(endpoint.UserInfo) && string.IsNullOrEmpty(endpoint.Query) && string.IsNullOrEmpty(endpoint.Fragment)
        && settings.TimeoutSeconds is >= 1 and <= 15;
    private async Task<CopilotModelResult> Select(CopilotModelInput input, CancellationToken token)
    {
        var settings = options.Value;
        if (!settings.Enabled) return new("disabled");
        if (string.IsNullOrWhiteSpace(settings.Model) || settings.Model.Length > 200
            || !Uri.TryCreate(settings.Endpoint, UriKind.Absolute, out var endpoint) || endpoint.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(endpoint.UserInfo) || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || settings.TimeoutSeconds is < 1 or > 15) return new("unconfigured");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(token);
        budget.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var schema = new
            {
                type = "object", properties = new { topic = new { type = "string", @enum = CopilotTopics.All } },
                required = new[] { "topic" }, additionalProperties = false
            };
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            if (!string.IsNullOrEmpty(settings.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
            request.Content = JsonContent.Create(new
            {
                model = settings.Model, stream = false, format = schema,
                messages = new[]
                {
                    new { role = "system", content = CopilotPrompts.System + JsonSerializer.Serialize(schema) },
                    new { role = "user", content = JsonSerializer.Serialize(input, new JsonSerializerOptions(JsonSerializerDefaults.Web)) }
                },
                options = new { temperature = 0, num_predict = 64 }
            });
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, budget.Token);
            if (!response.IsSuccessStatusCode) return new("unavailable");
            // Bound both declared and streamed size; a provider can omit Content-Length.
            if (response.Content.Headers.ContentLength > 32768) return new("invalid_response");
            await using var stream = await response.Content.ReadAsStreamAsync(budget.Token);
            using var bytes = new MemoryStream(); var buffer = new byte[4096];
            while (true)
            {
                var read = await stream.ReadAsync(buffer, budget.Token); if (read == 0) break;
                if (bytes.Length + read > 32768) return new("invalid_response");
                bytes.Write(buffer, 0, read);
            }
            using var document = JsonDocument.Parse(bytes.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("done", out var done) || done.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String) return new("invalid_response");
            using var selected = JsonDocument.Parse(content.GetString()!);
            var value = selected.RootElement;
            if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Count() != 1
                || !value.TryGetProperty("topic", out var topic) || topic.ValueKind != JsonValueKind.String
                || !CopilotTopics.All.Contains(topic.GetString())) return new("invalid_response");
            return new("available", topic.GetString(), Tokens("prompt_eval_count"), Tokens("eval_count"));
            long? Tokens(string name) => root.TryGetProperty(name, out var count) && count.TryGetInt64(out var value) && value is >= 0 and <= 10000000 ? value : null;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { return new("timeout"); }
        catch (Exception error) when (error is HttpRequestException or JsonException or InvalidOperationException or FormatException or IOException)
        { return new("unavailable"); }
    }
}
