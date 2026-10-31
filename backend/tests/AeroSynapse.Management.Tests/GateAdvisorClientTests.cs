using System.Net;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Infrastructure.Simulation;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class GateAdvisorClientTests
{
    private static readonly Guid A = Guid.Parse("72000000-0000-0000-0000-000000000001"), B = Guid.Parse("72000000-0000-0000-0000-000000000002");
    private static readonly Guid G1 = Guid.Parse("72000000-0000-0000-0000-000000000010"), G2 = Guid.Parse("72000000-0000-0000-0000-000000000011");
    private static JourneyFlight Flight(Guid id, double arrival, double departure) =>
        new(id, G1, arrival, arrival + 5, departure - 5, departure, 0, 4, false, false, null, 0, [G1, G2]);
    // A leaves G1 at 30 and B arrives there at 40; A is reported 20 minutes late at minute 5.
    private static readonly GateAdviceRequest Request = new("1.0", 5, [Flight(A, 10, 30), Flight(B, 40, 60)], [], new(A, 20));
    private static JourneyFlightPlan Plan(Guid id, Guid gate, double arrival, double departure, double delay = 0) =>
        new(id, gate, arrival, arrival + 5, departure - 5, departure, delay, false);
    private static readonly JourneyFlightPlan[] Before = [Plan(A, G1, 10, 30), Plan(B, G1, 40, 60)];
    private static GateAdvice Advice(params JourneyFlightPlan[] recommended) =>
        new("1.0", 5, "optimal", Before, recommended, [Plan(A, G1, 30, 50, 20), Plan(B, G1, 50, 70, 10)]);

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    { protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage message, CancellationToken token) => send(message, token); }
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static GateAdvisorClient Client(HttpStatusCode status, string body, Action<HttpRequestMessage>? inspect = null) =>
        new(new HttpClient(new Handler((message, _) =>
        {
            inspect?.Invoke(message);
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        })) { BaseAddress = new("http://intelligence/") });

    [Fact]
    public async Task Returns_a_plan_that_moves_a_flight_rather_than_delaying_the_next()
    {
        var moved = Advice(Plan(A, G2, 30, 50, 20), Plan(B, G1, 40, 60));
        var advice = await Client(HttpStatusCode.OK, JsonSerializer.Serialize(moved, ContractJson.Options),
            message => Assert.Equal("/optimization/gates/v1/advice", message.RequestUri!.AbsolutePath)).AdviseAsync(Request, Token);
        Assert.Equal(G2, advice.Recommended.Single(p => p.FlightId == A).GateId);
    }

    public static TheoryData<string> Untrusted => ["shared gate", "ineligible gate", "delay ignored", "missing flight", "moved without moves"];

    [Theory]
    [MemberData(nameof(Untrusted))]
    public async Task Rejects_plans_that_break_the_rules_it_was_asked_to_keep(string kind)
    {
        var other = Guid.Parse("72000000-0000-0000-0000-000000000099");
        var advice = kind switch
        {
            "shared gate" => Advice(Plan(A, G1, 30, 50, 20), Plan(B, G1, 40, 60)),
            "ineligible gate" => Advice(Plan(A, other, 30, 50, 20), Plan(B, G1, 40, 60)),
            "delay ignored" => Advice(Plan(A, G2, 10, 30), Plan(B, G1, 40, 60)),
            "missing flight" => Advice(Plan(A, G2, 30, 50, 20)),
            _ => Advice(Plan(A, G2, 30, 50, 20), Plan(B, G1, 40, 60)) with { WithoutMoves = [Plan(A, G2, 30, 50, 20), Plan(B, G1, 40, 60)] },
        };
        await Assert.ThrowsAsync<GateAdvisorUnavailableException>(() =>
            Client(HttpStatusCode.OK, JsonSerializer.Serialize(advice, ContractJson.Options)).AdviseAsync(Request, Token));
    }

    [Fact]
    public async Task A_timeline_conflict_is_the_operators_to_fix_not_an_outage()
    {
        var failure = await Assert.ThrowsAsync<ScenarioConflictException>(() =>
            Client(HttpStatusCode.Conflict, """{"code":"event_conflict","message":"departed"}""").AdviseAsync(Request, Token));
        Assert.Contains("departed", failure.Message);
        await Assert.ThrowsAsync<GateAdvisorUnavailableException>(() => Client(HttpStatusCode.ServiceUnavailable, "{}").AdviseAsync(Request, Token));
    }
}
