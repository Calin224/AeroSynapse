using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

// Staff schedules: the Python engine's scheduled run must correlate here, minute for minute.
public sealed class StaffScheduleTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "events", "v1");
    private static readonly Guid ControlB = Guid.Parse("60000000-0000-0000-0000-000000000102");
    private static JsonElement Example(string name) => JsonSerializer.SerializeToElement(JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "examples", name + ".json")))!);
    private static SimulationRequest Request() => ContractJson.Read<SimulationRequest>(Example("schedule-request"), "SimulationRequest");
    private static SimulationResponse Response() => ContractJson.Read<SimulationResponse>(Example("schedule-response"), "SimulationResponse");

    [Fact]
    public void Python_scheduled_run_roundtrips_and_correlates_with_its_request()
    {
        var request = Request(); var response = Response();
        Assert.True(JsonElement.DeepEquals(Example("schedule-request"), JsonSerializer.SerializeToElement(request, ContractJson.Options)));
        Assert.True(JsonElement.DeepEquals(Example("schedule-response"), JsonSerializer.SerializeToElement(response, ContractJson.Options)));
        Assert.Empty(VersionedContract.Validate("KpiSimulationResponse", Example("schedule-response")));
        Assert.Empty(ContractCorrelation.Validate(request, response));
    }

    [Fact]
    public void The_timeline_follows_the_schedule_and_its_outages()
    {
        var timeline = new EventTimeline(Request());
        Assert.Equal([1, 1, 3, 3, 2], new[] { 0, 29, 30, 59, 60 }.Select(minute => timeline.Capacity(ControlB, minute)));
        Assert.Equal(3, timeline.Nominal[ControlB]);
        var rows = Response().KpiResult!.Snapshots.Where(row => row.ZoneId == ControlB).ToArray();
        Assert.All(rows, row => Assert.Equal(timeline.Capacity(ControlB, row.Minute), row.EffectiveCapacity));
    }

    [Fact]
    public void Effective_capacity_must_match_the_schedule()
    {
        var response = Response();
        var kpis = response.KpiResult!;
        var snapshots = kpis.Snapshots.Select(row => row.ZoneId == ControlB && row.Minute == 40 ? row with { EffectiveCapacity = 1 } : row).ToArray();
        Assert.NotEmpty(ContractCorrelation.Validate(Request(), response with { KpiResult = kpis with { Snapshots = snapshots } }));
    }

    [Fact]
    public void Scheduled_checkpoints_take_no_staff_reduction_and_resume_only_ends_an_outage()
    {
        var context = new EventContext(90, new Dictionary<Guid, int> { [ControlB] = 3 }, new Dictionary<Guid, double>(),
            new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid> { ControlB });
        ScheduledEvent Event(string type, int minute, int number, int? staff = null) =>
            new(Guid.Parse($"90000000-0000-0000-0000-{number:D12}"), type, minute, ControlB, StaffCount: staff);
        Assert.Equal([(0, "checkpointId")], EventContractValidation.Conflicts([Event("staff_reduction", 40, 1, 1)], context));
        Assert.Equal([(0, "checkpointId")], EventContractValidation.Conflicts([Event("resume", 40, 1)], context));
        Assert.Empty(EventContractValidation.Conflicts([Event("checkpoint_unavailable", 30, 1), Event("resume", 40, 2)], context));
    }
}
