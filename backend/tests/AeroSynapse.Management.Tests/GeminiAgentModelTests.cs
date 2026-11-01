using System.Net;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Infrastructure.Execution;
using Microsoft.Extensions.Options;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class GeminiAgentModelTests
{
    private sealed class Reply(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Sent;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Request = request; Sent = await request.Content!.ReadAsStringAsync(token);
            return new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly AgentPrompt Prompt = new(Guid.NewGuid(), Guid.NewGuid(), 15, "00:15", new { queued = 40 },
        [new("none", "none", "Nicio schimbare acum"), new("move-1-1", "staff_move", "Mută un operator de la A la B")], 5);

    private static (GeminiAgentModel Model, Reply Handler) Model(HttpStatusCode status, string body, string key = "test-key")
    {
        var handler = new Reply(status, body);
        return (new GeminiAgentModel(new HttpClient(handler), Options.Create(new AgentOptions { ApiKey = key })), handler);
    }

    private static string Answer(string text) => JsonSerializer.Serialize(new
    {
        candidates = new[] { new { content = new { parts = new[] { new { text } } } } },
        usageMetadata = new { promptTokenCount = 120, candidatesTokenCount = 18 }
    });

    [Fact]
    public async Task The_model_picks_an_offered_option_under_a_schema_and_the_key_stays_in_a_header()
    {
        var (model, handler) = Model(HttpStatusCode.OK, Answer("""{"optionId":"move-1-1","reason":"Coada de la B are 40 de pasageri."}"""));
        var choice = await model.ChooseAsync(Prompt, TestContext.Current.CancellationToken);
        Assert.Equal(new AgentChoice("chosen", "move-1-1", "Coada de la B are 40 de pasageri.", "gemini-3.8-flash", 120, 18), choice);
        Assert.Equal("test-key", handler.Request!.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("test-key", handler.Request.RequestUri!.ToString());
        Assert.Contains("\"enum\":[\"none\",\"move-1-1\"]", handler.Sent);
        Assert.Contains("\"thinkingLevel\":\"low\"", handler.Sent);
        Assert.DoesNotContain("thinkingBudget", handler.Sent);
    }

    [Fact]
    public async Task A_gemini_2_model_keeps_the_legacy_thinking_budget()
    {
        var handler = new Reply(HttpStatusCode.OK, Answer("""{"optionId":"none","reason":"x"}"""));
        var model = new GeminiAgentModel(new HttpClient(handler), Options.Create(new AgentOptions { ApiKey = "k", Model = "gemini-2.5-flash" }));
        await model.ChooseAsync(Prompt, TestContext.Current.CancellationToken);
        Assert.Contains("\"thinkingBudget\":0", handler.Sent);
        Assert.DoesNotContain("thinkingLevel", handler.Sent);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, """{"optionId":"open-everything","reason":"x"}""", "invalid_response")]
    [InlineData(HttpStatusCode.PaymentRequired, "{}", "quota_exhausted")]
    [InlineData(HttpStatusCode.TooManyRequests, "{}", "quota_exhausted")]
    [InlineData(HttpStatusCode.InternalServerError, "{}", "unavailable")]
    public async Task Anything_but_an_offered_option_is_no_choice(HttpStatusCode status, string text, string expected)
    {
        var (model, _) = Model(status, status == HttpStatusCode.OK ? Answer(text) : text);
        var choice = await model.ChooseAsync(Prompt, TestContext.Current.CancellationToken);
        Assert.Equal(expected, choice.Status);
        Assert.Null(choice.OptionId);
    }

    [Fact]
    public async Task Without_a_key_nothing_is_sent()
    {
        var (model, handler) = Model(HttpStatusCode.OK, "{}", key: "");
        Assert.Equal("unconfigured", (await model.ChooseAsync(Prompt, TestContext.Current.CancellationToken)).Status);
        Assert.Null(handler.Request);
        Assert.False(model.Status.ModelConfigured);
    }
    [Fact]
    public async Task Advice_is_only_words_for_the_offered_options_with_readable_diacritics()
    {
        var text = """{"advice":"Adaugă un operator la CP-1.","options":[{"id":"add-1","explanation":"Așteptarea scade."},{"id":"invent","explanation":"y"}]}""";
        var (model, handler) = Model(HttpStatusCode.OK, Answer(text));
        var advice = await model.AdviseAsync(new { action = "Adaugă un operator la CP-1" }, ["add-1", "save"], TestContext.Current.CancellationToken);
        Assert.Equal("chosen", advice.Status);
        Assert.Equal("Adaugă un operator la CP-1.", advice.Advice);
        Assert.Equal([new OptimizationAdviceOption("add-1", "Așteptarea scade.")], advice.Options);
        Assert.Contains("\"enum\":[\"add-1\",\"save\"]", handler.Sent);
        // No pick and no titles are asked for: the recommendation and the card titles come from code.
        Assert.DoesNotContain("recommendedId", handler.Sent);
        Assert.DoesNotContain("title", handler.Sent);
        // The facts reach the model as "ă", not "\\u0103", which a small model reads far worse.
        Assert.Contains("Adaug\u0103 un operator", JsonDocument.Parse(handler.Sent!).RootElement.GetProperty("contents")[0].GetProperty("parts")[0].GetProperty("text").GetString());

        var (empty, _) = Model(HttpStatusCode.OK, Answer("""{"advice":"","options":[{"id":"invent","explanation":"y"}]}"""));
        Assert.Equal("invalid_response", (await empty.AdviseAsync(new { }, ["add-1"], TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task A_local_ollama_model_needs_no_key_and_gets_a_standard_json_schema()
    {
        var reply = JsonSerializer.Serialize(new
        {
            message = new { role = "assistant", content = """{"optionId":"move-1-1","reason":"Coada de la B crește."}""" },
            done = true, prompt_eval_count = 90, eval_count = 12
        });
        var handler = new Reply(HttpStatusCode.OK, reply);
        var model = new GeminiAgentModel(new HttpClient(handler), Options.Create(new AgentOptions
            { Provider = "ollama", Model = "qwen2.5:7b", Endpoint = "http://host.docker.internal:11434" }));
        Assert.True(model.Status.ModelConfigured);
        var choice = await model.ChooseAsync(Prompt, TestContext.Current.CancellationToken);
        Assert.Equal(new AgentChoice("chosen", "move-1-1", "Coada de la B crește.", "qwen2.5:7b", 90, 12), choice);
        Assert.Equal("http://host.docker.internal:11434/api/chat", handler.Request!.RequestUri!.ToString());
        Assert.False(handler.Request.Headers.Contains("x-goog-api-key"));
        Assert.Contains("\"type\":\"object\"", handler.Sent);
        Assert.Contains("\"enum\":[\"none\",\"move-1-1\"]", handler.Sent);
        Assert.DoesNotContain("OBJECT", handler.Sent);

        var (missing, _) = (new GeminiAgentModel(new HttpClient(new Reply(HttpStatusCode.NotFound, "{}")), Options.Create(new AgentOptions
            { Provider = "ollama", Model = "nepulled", Endpoint = "http://localhost:11434" })), 0);
        Assert.Equal("unavailable", (await missing.ChooseAsync(Prompt, TestContext.Current.CancellationToken)).Status);
    }
}
