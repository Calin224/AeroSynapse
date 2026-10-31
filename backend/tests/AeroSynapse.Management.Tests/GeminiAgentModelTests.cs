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
        Assert.Equal(new AgentChoice("chosen", "move-1-1", "Coada de la B are 40 de pasageri.", "gemini-2.5-flash", 120, 18), choice);
        Assert.Equal("test-key", handler.Request!.Headers.GetValues("x-goog-api-key").Single());
        Assert.DoesNotContain("test-key", handler.Request.RequestUri!.ToString());
        Assert.Contains("\"enum\":[\"none\",\"move-1-1\"]", handler.Sent);
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
}
