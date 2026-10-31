using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Contract.Tests;

// Check-in desks before security: the Python stage must correlate with its request and with the control arrivals.
public sealed class CheckInTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "kpis", "v1");
    private static JsonElement Example(string name) => JsonSerializer.SerializeToElement(JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "examples", name + ".json")))!);
    private static SimulationRequest Request() => ContractJson.Read<SimulationRequest>(Example("check-in-request"), "SimulationRequest");
    private static SimulationResponse Response() => ContractJson.Read<SimulationResponse>(Example("check-in-response"), "SimulationResponse");
    private static SimulationResponse With(SimulationResponse response, Func<CheckInResult, CheckInResult> change) =>
        response with { CheckInResult = change(response.CheckInResult!) };
    private static CheckInResult Passengers(CheckInResult result, Func<IReadOnlyList<CheckInPassenger>, IEnumerable<CheckInPassenger>> change) =>
        result with { Passengers = change(result.Passengers).ToArray() };

    [Fact]
    public void Python_check_in_run_correlates_and_keeps_late_passengers_as_control_demand()
    {
        var request = Request(); var response = Response();
        Assert.Empty(ContractCorrelation.Validate(request, response));
        var absent = CheckInCorrelation.Absent(response);
        Assert.NotEmpty(absent);
        Assert.Equal(response.ProcessResult!.PopulationCount - absent.Length, response.ProcessResult.ArrivedCount);
        Assert.Equal(absent.Length, response.KpiResult!.NotArrivedCount);
    }

    [Fact]
    public void A_control_arrival_must_be_the_check_in_finish_plus_the_walk()
    {
        var response = Response();
        var joined = response.CheckInResult!.Passengers.First(person => person.SecurityArrivalMinute is not null);
        Assert.False(CheckInCorrelation.Valid(Request(), With(response, result => Passengers(result, people =>
            people.Select(person => person.Id == joined.Id ? person with { SecurityArrivalMinute = person.SecurityArrivalMinute + 1 } : person)))));
    }

    [Fact]
    public void A_passenger_still_at_check_in_cannot_reach_a_control()
    {
        var response = Response();
        var joined = response.CheckInResult!.Passengers.First(person => person.SecurityArrivalMinute is not null);
        Assert.False(CheckInCorrelation.Valid(Request(), With(response, result => Passengers(result, people =>
            people.Select(person => person.Id == joined.Id ? person with { SecurityArrivalMinute = null } : person)))));
    }

    [Fact]
    public void Desks_serve_in_arrival_order_and_never_more_than_staffed()
    {
        var response = Response();
        var served = response.CheckInResult!.Passengers.Where(person => person.FinishedMinute is not null)
            .OrderBy(person => person.TerminalArrivalMinute).ThenBy(person => person.Id, StringComparer.Ordinal).ToArray();
        // Swapping two service slots breaks first come, first served.
        var (first, second) = (served[^3], served[^2]);
        Assert.False(CheckInCorrelation.Valid(Request(), With(response, result => Passengers(result, people => people.Select(person =>
            person.Id == first.Id ? person with { StartedMinute = second.StartedMinute, FinishedMinute = second.StartedMinute + person.ServiceMinutes }
            : person.Id == second.Id ? person with { StartedMinute = first.StartedMinute, FinishedMinute = first.StartedMinute + person.ServiceMinutes }
            : person)))));
        Assert.False(CheckInCorrelation.Valid(Request(), With(response, result => result with { Capacity = result.Capacity + 1 })));
    }

    [Fact]
    public void Desk_snapshots_are_re_derived_from_the_passengers()
    {
        var response = Response();
        Assert.False(CheckInCorrelation.Valid(Request(), With(response, result => result with
        {
            Snapshots = result.Snapshots.Select((row, index) => index == 3 ? row with { QueuedPassengers = row.QueuedPassengers + 1, ArrivedPassengers = row.ArrivedPassengers + 1 } : row).ToArray()
        })));
    }

    [Fact]
    public void Passengers_stranded_at_check_in_count_in_their_controls_boarding_risk()
    {
        var request = Request(); var response = Response();
        var stranded = CheckInCorrelation.Absent(response);
        var zone = stranded[0].CheckpointId;
        var final = response.KpiResult!.Snapshots.Last(row => row.ZoneId == zone);
        Assert.Equal(response.ProcessResult!.Passengers.Count(person => person.CheckpointId == zone && person.BoardingCloseMinute is not null)
            + stranded.Count(person => person.CheckpointId == zone), final.BoardingRiskPopulationCount);
        // Their flight closes within the horizon and they never reach the gate, so they are at risk.
        Assert.True(final.MissedDeadlineUnfinishedCount >= stranded.Count(person => person.CheckpointId == zone));
        Assert.True(KpiCorrelation.Valid(request, response));
        // Leaving them out of the risk population, as before, no longer correlates.
        var without = response.KpiResult with { Snapshots = response.KpiResult.Snapshots.Select(row => row == final
            ? row with { BoardingRiskPopulationCount = row.BoardingRiskPopulationCount - stranded.Count(person => person.CheckpointId == zone) } : row).ToArray() };
        Assert.False(KpiCorrelation.Valid(request, response with { KpiResult = without }));
    }

    [Fact]
    public void Surge_passengers_check_in_when_they_enter_during_their_events_window()
    {
        var request = ContractJson.Read<SimulationRequest>(Example("check-in-events-request"), "SimulationRequest");
        var response = ContractJson.Read<SimulationResponse>(Example("check-in-events-response"), "SimulationResponse");
        Assert.Empty(ContractCorrelation.Validate(request, response));
        var surged = response.CheckInResult!.Passengers.First(person => person.Id.StartsWith("demo-surge-", StringComparison.Ordinal));
        var surge = request.Events!.Single(item => item.Type == "passenger_surge");
        Assert.InRange(surged.TerminalArrivalMinute, surge.Minute, surge.Minute + surge.WindowMinutes!.Value);
        Assert.True(CheckInCorrelation.Valid(request, response));
        // Without a flight there is no hold baggage, so a surge passenger at the desks no longer correlates.
        var unflown = request with { Events = request.Events.Select(item => item == surge ? item with { FlightId = null } : item).ToArray() };
        Assert.False(CheckInCorrelation.Valid(unflown, response));
    }

    [Fact]
    public void A_request_without_check_in_must_not_report_one()
    {
        var request = Request() with { CheckIn = null };
        Assert.False(CheckInCorrelation.Valid(request, Response()));
    }

    private static readonly CheckInArea Desks = new() { Id = Guid.NewGuid(), Code = "CHECK-IN", Name = "Check-in", DeskCount = 8, ServiceSeconds = 120, WalkingSeconds = 90 };
    private static readonly SceneDetails Layout = new(new(Guid.NewGuid(), "DEMO", "Demo", "", true), [], [], [], [], [], [Desks]);
    private static ScenarioInputs Inputs(int staff = 3, decimal share = .6m, Guid? area = null) =>
        new() { CheckIn = new(area ?? Desks.Id, staff, share) };

    [Fact]
    public void Scenario_check_in_compiles_the_layout_desks_for_the_kpi_engine_only()
    {
        Assert.Equal(new CheckInInput(Desks.Id, 3, .5, .6, 1.5), CheckInScenarios.Input(Inputs(), Layout, KpiConventions.Engine));
        Assert.Null(CheckInScenarios.Input(new ScenarioInputs(), Layout, KpiConventions.Engine));
        Assert.Contains("inputs.checkIn", Assert.Throws<ScenarioValidationException>(() => CheckInScenarios.Input(Inputs(), Layout, "simpy-1.0")).Errors.Keys);
        // Check-in acts on individual passengers, so an aggregate scenario derives default profiles for it.
        Assert.True(IncidentScenarios.DerivesProfiles(Inputs()));
        Assert.False(IncidentScenarios.DerivesProfiles(new ScenarioInputs()));
    }

    [Theory]
    [InlineData(9, 0.6, false, "inputs.checkIn.staffCount")]
    [InlineData(-1, 0.6, false, "inputs.checkIn.staffCount")]
    [InlineData(3, 1.2, false, "inputs.checkIn.holdBaggageShare")]
    [InlineData(3, 0.6, true, "inputs.checkIn.areaId")]
    public void Scenario_check_in_stays_within_the_layout_desks(int staff, double share, bool unknownArea, string field)
    {
        CheckInScenarios.Validate(Inputs(), Layout);
        var error = Assert.Throws<ScenarioValidationException>(() => CheckInScenarios.Validate(Inputs(staff, (decimal)share, unknownArea ? Guid.NewGuid() : null), Layout));
        Assert.Equal([field], error.Errors.Keys);
    }
}
