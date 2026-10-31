using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class JourneyTests
{
    private static JsonElement Example(string name) => JsonSerializer.SerializeToElement(JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "contracts", "journeys", "v1", "examples", name + ".json")))!);
    private static SimulationRequest Request(string name = "day") => ContractJson.Read<SimulationRequest>(Example(name + "-request"), "SimulationRequest");
    private static SimulationResponse Response(string name = "day") => ContractJson.Read<SimulationResponse>(Example(name + "-response"), "SimulationResponse");

    [Theory]
    [InlineData("day")]
    [InlineData("connection-delay")]
    [InlineData("check-in")]
    public void Python_journeys_roundtrip_and_correlate_with_the_exact_capture(string name)
    {
        var request = Request(name); var response = Response(name);
        Assert.True(JsonElement.DeepEquals(Example(name + "-request"), JsonSerializer.SerializeToElement(request, ContractJson.Options)));
        Assert.True(JsonElement.DeepEquals(Example(name + "-response"), JsonSerializer.SerializeToElement(response, ContractJson.Options)));
        Assert.Empty(ContractCorrelation.Validate(request, response));
        Assert.Equal(16, response.JourneyResult!.PopulationCount);
        Assert.Equal(14, response.JourneyResult.UpstreamResult.ProcessResult!.PopulationCount);
    }

    [Fact]
    public void Boarding_and_passport_service_cannot_be_fabricated()
    {
        var request = Request(); var response = Response(); var result = response.JourneyResult!;
        var passenger = result.Passengers.First(p => p.BoardedMinute is not null);
        var forged = result with { Passengers = result.Passengers.Select(p => p.Id == passenger.Id ? p with { BoardedMinute = p.GateArrivalMinute } : p).ToArray() };
        Assert.NotEmpty(ContractCorrelation.Validate(request, response with { JourneyResult = forged }));
        forged = result with { Passengers = result.Passengers.Select(p => p.Id == passenger.Id
            ? p with { Visits = p.Visits.Where(v => v.Stage != "passport").ToArray() } : p).ToArray() };
        Assert.NotEmpty(ContractCorrelation.Validate(request, response with { JourneyResult = forged }));
    }

    [Fact]
    public void Snapshots_and_upstream_security_are_checked_before_persistence()
    {
        var request = Request(); var response = Response(); var result = response.JourneyResult!;
        Assert.NotEmpty(ContractCorrelation.Validate(request, response with { JourneyResult = result with
        {
            Snapshots = result.Snapshots.Select((s, i) => i == 5 ? s with { GateQueuedCount = s.GateQueuedCount + 1 } : s).ToArray()
        } }));
        var upstream = result.UpstreamResult;
        Assert.NotEmpty(ContractCorrelation.Validate(request, response with { JourneyResult = result with
        {
            UpstreamResult = upstream with { Metrics = upstream.Metrics.Select(m => m with { ProcessedPassengers = m.ProcessedPassengers + 1 }).ToArray() }
        } }));
    }

    [Fact]
    public void A_later_replan_cannot_move_an_aircraft_that_has_arrived()
    {
        var request = Request(); var response = Response(); var result = response.JourneyResult!;
        var plan = result.Plans[1]; var active = plan.Flights.First(f => f.ArrivalMinute < plan.Minute && f.DepartureMinute > plan.Minute);
        var other = request.Journey!.Flights.Single(f => f.FlightId == active.FlightId).EligibleGateIds.First(g => g != active.GateId);
        var forged = result with { Plans = result.Plans.Select((p, i) => i == 1 ? p with
        {
            Flights = p.Flights.Select(f => f.FlightId == active.FlightId ? f with { GateId = other } : f).ToArray()
        } : p).ToArray() };
        Assert.NotEmpty(ContractCorrelation.Validate(request, response with { JourneyResult = forged }));
    }

    [Fact]
    public void Departure_can_be_delayed_after_boarding_closed_without_reopening_it()
    {
        var request = Request(); var response = Response();
        var last = response.JourneyResult!.Plans[^1];
        var flight = last.Flights.Single(f => f.FlightId == request.Journey!.Flights[0].FlightId);
        Assert.Equal(238, last.Minute);
        Assert.Equal(235, flight.BoardingCloseMinute);
        Assert.Equal(245, flight.DepartureMinute);
        Assert.Empty(ContractCorrelation.Validate(request, response));
    }

    [Fact]
    public void Missing_and_unrequested_journey_reports_are_rejected()
    {
        var request = Request(); var response = Response();
        Assert.NotEmpty(ContractCorrelation.Validate(request, response with { JourneyResult = null }));
        Assert.NotEmpty(ContractCorrelation.Validate(request with { Journey = null }, response));
    }

    [Fact]
    public void Cyclic_rotations_and_connections_exceeding_the_outbound_cohort_are_rejected()
    {
        var value = JsonNode.Parse(Example("day-request").GetRawText())!;
        value["journey"]!["flights"]![2]!["previousFlightId"] = value["journey"]!["flights"]![3]!["flightId"]!.DeepClone();
        Assert.NotEmpty(VersionedContract.Validate("KpiSimulationRequest", JsonSerializer.SerializeToElement(value)));
        value = JsonNode.Parse(Example("day-request").GetRawText())!;
        value["journey"]!["connections"]![0]!["passengerCount"] = 5;
        Assert.NotEmpty(VersionedContract.Validate("KpiSimulationRequest", JsonSerializer.SerializeToElement(value)));
        value = JsonNode.Parse(Example("day-request").GetRawText())!;
        value["journey"]!["flights"]![1]!["previousFlightId"] = value["journey"]!["flights"]![3]!["previousFlightId"]!.DeepClone();
        Assert.NotEmpty(VersionedContract.Validate("KpiSimulationRequest", JsonSerializer.SerializeToElement(value)));
        value = JsonNode.Parse(Example("day-request").GetRawText())!;
        value["passengerGeneration"]!["flights"]!.AsArray().Add(value["passengerGeneration"]!["flights"]![0]!.DeepClone());
        Assert.NotEmpty(VersionedContract.Validate("KpiSimulationRequest", JsonSerializer.SerializeToElement(value)));
    }
}
