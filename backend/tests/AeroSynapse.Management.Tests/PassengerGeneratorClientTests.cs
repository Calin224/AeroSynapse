using System.Net;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Simulation;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class PassengerGeneratorClientTests
{
    private static readonly Guid FlightId = Guid.NewGuid(), CheckpointId = Guid.NewGuid(), RouteId = Guid.NewGuid();
    private static readonly PassengerDistribution Constant = new("constant", 1, 1, 1, 1, 1);
    private static readonly PassengerGenerationRequest Request = new("1.0", 42, 10,
        [new(CheckpointId, 1, 1)], [new(FlightId, 2, 10, false, CheckpointId, RouteId, 1)],
        [new(FlightId, CheckpointId, "full", 2, Constant, Constant, Constant,
            [new("solo", 1, 1, 1, 1, 1)], [])]);
    private static PassengerGenerationResult Result => new("1.0", 42, true, "PCG64", "2.5.4", new string('a', 64),
        2, 2, false, 0, 2, false, [new(FlightId, 2, 2, 0, "full", 2, 0, true)],
        [new(CheckpointId, 2, 2, 1, 1, 1, true, [2])],
        Enumerable.Range(1, 2).Select(index => new VirtualPassenger($"demo-pax-{index}", FlightId, CheckpointId, RouteId,
            $"demo-group-{index}", "solo", 1, 1, 1, false, true)).ToArray());

    private sealed class Handler(HttpStatusCode status, string json) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Path = request.RequestUri!.PathAndQuery;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }

    [Fact]
    public async Task Complete_correlated_page_is_accepted_and_query_is_forwarded()
    {
        var handler = new Handler(HttpStatusCode.OK, JsonSerializer.Serialize(Result, ContractJson.Options));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://intelligence/") };
        var result = await new PassengerGeneratorClient(http).GenerateAsync(Request, 0, 100, TestContext.Current.CancellationToken);
        Assert.Equal("/passengers/v1/generate?offset=0&limit=100", handler.Path);
        Assert.Equal(2, result.ReturnedCount); Assert.Equal(Result.Fingerprint, result.Fingerprint);
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("null-passengers")]
    [InlineData("null-batch")]
    [InlineData("unknown-flight")]
    [InlineData("zero-service")]
    [InlineData("wrong-total")]
    [InlineData("unknown-demand")]
    [InlineData("negative-demand")]
    [InlineData("missing-quarters")]
    [InlineData("quarters-total")]
    public async Task Inconsistent_generator_responses_report_unavailability(string invalid)
    {
        var result = Result;
        result = invalid switch
        {
            "truncated" => result with { ReturnedCount = 1, Passengers = result.Passengers.Take(1).ToArray() },
            "null-passengers" => result with { Passengers = null! },
            "null-batch" => result with { Batches = [null!] },
            "unknown-flight" => result with { Passengers = [result.Passengers[0] with { FlightId = Guid.NewGuid() }, result.Passengers[1]] },
            "zero-service" => result with { Passengers = [result.Passengers[0] with { ServiceMinutes = 0 }, result.Passengers[1]] },
            "wrong-total" => result with { ConfiguredCount = 100 },
            "unknown-demand" => result with { CheckpointDemand = [result.CheckpointDemand[0] with { CheckpointId = Guid.NewGuid() }] },
            "negative-demand" => result with { CheckpointDemand = [result.CheckpointDemand[0] with { MeanServiceMinutes = -1 }] },
            "missing-quarters" => result with { CheckpointDemand = [result.CheckpointDemand[0] with { ArrivalsPerQuarterHour = null }] },
            "quarters-total" => result with { CheckpointDemand = [result.CheckpointDemand[0] with { ArrivalsPerQuarterHour = [1] }] },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, JsonSerializer.Serialize(result, ContractJson.Options)))
            { BaseAddress = new Uri("http://intelligence/") };
        await Assert.ThrowsAsync<PassengerGeneratorUnavailableException>(() =>
            new PassengerGeneratorClient(http).GenerateAsync(Request, 0, 100, TestContext.Current.CancellationToken));
    }

    private static PassengerFlowReport Report => new("1.0", 42, true, new string('b', 64), 10, 2,
        [new("security", 2, 2, 0, .5, 1, 3)], [new("standard", 2, 2, 0, 3, 4, .5, .25)]);

    [Fact]
    public async Task Flow_report_is_forwarded_and_checked_against_the_request()
    {
        var handler = new Handler(HttpStatusCode.OK, JsonSerializer.Serialize(Report, ContractJson.Options));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://intelligence/") };
        var report = await new PassengerGeneratorClient(http).AnalyzeFlowsAsync(Request, TestContext.Current.CancellationToken);
        Assert.Equal("/passengers/v1/flows", handler.Path);
        Assert.Equal(3, report.Stages[0].BlockedMinutes);
        foreach (var invalid in new[] { Report with { Segments = [Report.Segments[0] with { Passengers = 1 }] },
                     Report with { Segments = [Report.Segments[0] with { Segment = "transfer" }] },
                     Report with { Stages = [Report.Stages[0] with { QueuedPassengers = 1 }] }, Report with { IsSynthetic = false } })
        {
            using var other = new HttpClient(new Handler(HttpStatusCode.OK, JsonSerializer.Serialize(invalid, ContractJson.Options)))
                { BaseAddress = new Uri("http://intelligence/") };
            await Assert.ThrowsAsync<PassengerGeneratorUnavailableException>(() =>
                new PassengerGeneratorClient(other).AnalyzeFlowsAsync(Request, TestContext.Current.CancellationToken));
        }
    }

    [Fact]
    public async Task Routed_passenger_fields_must_match_the_requested_flow()
    {
        var routed = Result with { Passengers = Result.Passengers.Select(passenger => passenger with
            { Segment = "standard", TransferMinutes = 0, Stages = [new("security", 1)] }).ToArray() };
        var flow = new PassengerFlowConfig([new("standard", 1, false, "security", 1, 1)], [], []);
        using var http = new HttpClient(new Handler(HttpStatusCode.OK, JsonSerializer.Serialize(routed, ContractJson.Options)))
            { BaseAddress = new Uri("http://intelligence/") };
        var client = new PassengerGeneratorClient(http);
        Assert.Equal("standard", (await client.GenerateAsync(Request with { Flow = flow }, 0, 100, TestContext.Current.CancellationToken)).Passengers[0].Segment);
        await Assert.ThrowsAsync<PassengerGeneratorUnavailableException>(() => client.GenerateAsync(Request, 0, 100, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Validation_errors_preserve_the_generator_field_diagnostics()
    {
        var errors = new[] { VersionedContract.Error("/batches/0/modeledCount", "invalid_value") };
        using var http = new HttpClient(new Handler(HttpStatusCode.UnprocessableEntity,
            JsonSerializer.Serialize(VersionedContract.Response(errors), ContractJson.Options))) { BaseAddress = new Uri("http://intelligence/") };
        var failure = await Assert.ThrowsAsync<ContractValidationException>(() =>
            new PassengerGeneratorClient(http).GenerateAsync(Request, 0, 100, TestContext.Current.CancellationToken));
        Assert.Equal(errors, failure.Errors);
    }
}
