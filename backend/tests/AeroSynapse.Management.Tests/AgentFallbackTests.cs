using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class AgentFallbackTests
{
    private static readonly Guid Busy = Guid.NewGuid(), Quiet = Guid.NewGuid();
    private static readonly Dictionary<Guid, string> Names = new() { [Busy] = "CP-1", [Quiet] = "CP-2" };
    private static readonly ScenarioAgent Agent = new(StaffMoves: true, CheckInDesks: true, GateChoices: false, DecisionIntervalMinutes: 15, TransferMinutes: 5);

    private static AgentCheckpointState Control(Guid id, int staff, int queued, int arriving) =>
        new(id, staff, staff, true, 1, queued, 0, 0, arriving, null, 0, 0, null);

    private static AgentObservation Observe(int busyQueue, AgentCheckInState? checkIn = null) =>
        new("1.0", 15, [Control(Busy, 2, busyQueue, 30), Control(Quiet, 3, 0, 5)], checkIn);

    private static (AgentOption Option, string Reason) Decide(AgentObservation observation, int? desks = null) =>
        AgentCandidates.Fallback(observation, AgentCandidates.Periodic(observation, Agent, new HashSet<Guid>(), Names, desks), Names);

    [Fact]
    public void Short_queues_keep_everything_as_it_is()
    {
        var (option, reason) = Decide(Observe(busyQueue: 10));
        Assert.Equal("none", option.Kind);
        Assert.Equal("", reason);
    }

    [Fact]
    public void An_overloaded_control_gets_operators_from_a_quiet_one()
    {
        // 50 queued + 30 arriving against 2 operators × 1/min × 30 min = 60.
        var (option, reason) = Decide(Observe(busyQueue: 50));
        Assert.Equal("staff_move", option.Kind);
        Assert.Equal((Quiet, Busy), (option.FromCheckpointId, option.ToCheckpointId));
        Assert.Contains("CP-1", reason);
    }

    [Fact]
    public void With_nobody_to_move_the_agent_calls_just_enough_operators_from_the_reserve()
    {
        // Operation Bottleneck at 11:15: one operator at CP-1 with 465 queued, the other controls unstaffed.
        AgentObservation alone = new("1.0", 15, [new(Busy, 1, 1, true, 7, 465, 1, 103, 31, 3.3, 9.7, 0, 31), Control(Quiet, 0, 0, 0)], null);
        Assert.Equal(["none"], AgentCandidates.Periodic(alone, Agent, new HashSet<Guid>(), Names, null).Select(o => o.Id));
        var offered = AgentCandidates.Periodic(alone, Agent, new HashSet<Guid>(), Names, null, reserveLeft: 10);
        Assert.Equal(["none", "call-2", "call-5", "call-10"], offered.Select(o => o.Id));
        // 496 passengers against 3 operators × 7/min × 30 min = 630: two are enough.
        var (option, reason) = AgentCandidates.Fallback(alone, offered, Names);
        Assert.Equal(("staff_call", Busy, 2), (option.Kind, option.ToCheckpointId, option.Count));
        Assert.Contains("CP-1", reason);
        // Calls never exceed what is left of the reserve.
        Assert.Equal(["none", "call-2", "call-3"], AgentCandidates.Periodic(alone, Agent, new HashSet<Guid>(), Names, null, reserveLeft: 3).Select(o => o.Id));
    }

    [Fact]
    public void An_agent_that_moves_operators_gets_the_default_reserve_unless_one_is_set()
    {
        Assert.Equal(10, new ScenarioAgent(StaffMoves: true, CheckInDesks: false, GateChoices: false).Reserve);
        Assert.Equal(0, new ScenarioAgent(StaffMoves: true, CheckInDesks: false, GateChoices: false, ReserveOperators: 0).Reserve);
        Assert.Equal(0, new ScenarioAgent(StaffMoves: false, CheckInDesks: true, GateChoices: false).Reserve);
        // The default is derived, never written into the saved scenario.
        Assert.DoesNotContain("reserve", JsonSerializer.Serialize(new ScenarioAgent(true, false, false), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Called_operators_serve_after_the_transfer_without_leaving_any_control()
    {
        var request = new SimulationRequest("1.0", Guid.NewGuid(), Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UnixEpoch, "UTC", 60, 42, 100, 2,
            [new(Busy, 7, 1), new(Quiet, 8, 0)]);
        var call = new AgentOption("call-2", "staff_call", "", ToCheckpointId: Busy, Count: 2);
        var applied = AgentDecisionInputs.Apply(request, [(15, call)], 5);
        var schedule = Assert.Single(applied.StaffSchedules!);
        Assert.Equal(Busy, schedule.CheckpointId);
        Assert.Equal([new StaffStep(0, 1), new StaffStep(21, 3)], schedule.Steps);
        Assert.Equal(3, applied.Checkpoints.Single(c => c.CheckpointId == Busy).StaffCount);
    }

    private static KpiSnapshot Row(Guid zone, int minute, int queued, int samples = 0, double? mean = null, double? p95 = null) =>
        new("1.0", Guid.Empty, zone, "checkpoint", minute, minute, minute, "", 0, 0, 0, queued, 0, 0, 0, 0, 0, samples, 0, queued, 0, 0, 0, 0,
            mean, null, p95, null, null, null, null, null, 0, 0, 0, 0, null);

    [Fact]
    public void The_impact_is_measured_like_the_tower_tiles_and_names_what_changed()
    {
        // CP-1 peaks at minute 10 and is empty again from minute 30; CP-2 stays empty.
        var side = AgentImpactSummary.From([Row(Busy, 0, 0), Row(Busy, 10, 472), Row(Busy, 20, 100), Row(Busy, 30, 0),
            Row(Busy, 90, 0, samples: 300, mean: 12, p95: 18), Row(Quiet, 10, 0), Row(Quiet, 90, 0, samples: 100, mean: 4, p95: 6)]);
        Assert.Equal(new AgentImpactSide(10, 18, 472, 30), side);
        Assert.Null(AgentImpactSummary.From([Row(Busy, 0, 5), Row(Busy, 90, 40)]).QueueClearedMinute);
        var calls = new[] { new AgentOption("call-2", "staff_call", "", ToCheckpointId: Busy, Count: 2), new AgentOption("call-2", "staff_call", "", ToCheckpointId: Busy, Count: 2) };
        Assert.Equal(["4 operatori din rezervă la CP-1"], AgentImpactSummary.Changes(calls, Names));
    }

    [Fact]
    public void An_overloaded_check_in_opens_as_few_desks_as_bring_it_under_capacity()
    {
        // 40 + 30 against 2 desks × 1/min × 30 = 60: one more desk (90) is enough.
        var one = Decide(Observe(busyQueue: 0, new(Guid.NewGuid(), 2, 1, 40, 30, null)), desks: 6);
        Assert.Equal(("desk_open", 1), (one.Option.Kind, one.Option.Count));
        // 100 + 30 against 60: one more desk (90) is not, so two.
        var two = Decide(Observe(busyQueue: 0, new(Guid.NewGuid(), 2, 1, 100, 30, null)), desks: 6);
        Assert.Equal(("desk_open", 2), (two.Option.Kind, two.Option.Count));
    }
}
