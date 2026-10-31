using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class AgentQueueTests
{
    private static readonly Guid Busy = Guid.Parse("60000000-0000-0000-0000-000000000101");
    private static readonly Guid Spare = Guid.Parse("60000000-0000-0000-0000-000000000102");
    private static readonly Guid Donor = Guid.Parse("60000000-0000-0000-0000-000000000103");
    private static readonly Dictionary<Guid, string> Names = new() { [Busy] = "CP-1", [Spare] = "CP-2", [Donor] = "CP-3" };
    private static readonly ScenarioAgent Agent = new(true, false, false, 5, 3, ReserveOperators: 0);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static AgentCheckpointState Control(Guid id, int staff, int queued, bool available = true) =>
        new(id, staff, staff, available, 1, queued, 0, 0, 0, null, 0, 0, null);

    private static IReadOnlyList<AgentOption> Offered(AgentCheckpointState[] controls, int reserve = 0, params Guid[] frozen) =>
        AgentCandidates.Periodic(new("1.0", 10, controls, null), Agent, frozen.ToHashSet(), Names, null, reserve);

    [Fact]
    public void An_overloaded_queue_can_open_an_available_unstaffed_control_from_the_reserve()
    {
        var offered = Offered([Control(Busy, 1, 80), Control(Spare, 0, 0)], reserve: 1);
        var open = Assert.Single(offered, option => option.Kind == "checkpoint_open" && option.ToCheckpointId == Spare);
        Assert.Equal(Busy, open.FromCheckpointId);
        Assert.Equal(1, open.Count);
        Assert.Contains(new AgentPlanTarget(Spare, 0, 1), open.Staff!);
        Assert.InRange(open.PassengerCount!.Value, 1, 80);
        Assert.Contains("CP-2", open.Text);
    }

    [Fact]
    public void Opening_a_control_can_reuse_an_idle_operator_while_the_donor_keeps_at_least_one()
    {
        var offered = Offered([Control(Busy, 1, 80), Control(Spare, 0, 0), Control(Donor, 3, 0)]);
        var open = Assert.Single(offered, option => option.Kind == "checkpoint_open" && option.ToCheckpointId == Spare);
        Assert.Equal(0, open.Count);
        Assert.Contains(new AgentPlanTarget(Spare, 0, 1), open.Staff!);
        var donor = Assert.Single(open.Staff!, target => target.Target < target.Current);
        Assert.Equal(Donor, donor.CheckpointId);
        Assert.True(donor.Target >= 1);
        Assert.Equal(0, open.Staff!.Sum(target => target.Target - target.Current));
    }

    [Fact]
    public void Opening_requires_personnel_and_never_takes_the_last_operator_from_a_control()
    {
        var offered = Offered([Control(Busy, 1, 80), Control(Spare, 0, 0), Control(Donor, 1, 0)]);
        Assert.DoesNotContain(offered, option => option.Kind == "checkpoint_open");
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Frozen_or_unavailable_controls_are_not_opened(bool frozen, bool unavailable)
    {
        var offered = Offered([Control(Busy, 1, 80), Control(Spare, 0, 0, !unavailable)],
            reserve: 1, frozen ? [Spare] : []);
        Assert.DoesNotContain(offered, option => option.Kind == "checkpoint_open" && option.ToCheckpointId == Spare);
    }

    [Fact]
    public void Passengers_can_be_distributed_to_an_open_control_with_a_shorter_expected_wait()
    {
        var offered = Offered([Control(Busy, 1, 80), Control(Spare, 2, 0)]);
        var move = Assert.Single(offered, option => option.Kind == "queue_redistribution" && option.ToCheckpointId == Spare);
        Assert.Equal(Busy, move.FromCheckpointId);
        Assert.InRange(move.PassengerCount!.Value, 1, 80);
    }

    [Fact]
    public void Redistribution_does_not_send_passengers_to_a_longer_queue_or_an_unstaffed_control()
    {
        var offered = Offered([Control(Busy, 1, 80), Control(Spare, 1, 120), Control(Donor, 0, 0)]);
        Assert.DoesNotContain(offered, option => option.Kind == "queue_redistribution" && option.FromCheckpointId == Busy);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Redistribution_excludes_frozen_or_unavailable_destinations(bool frozen, bool unavailable)
    {
        var offered = Offered([Control(Busy, 1, 80), Control(Spare, 2, 0, !unavailable)],
            reserve: 0, frozen ? [Spare] : []);
        Assert.DoesNotContain(offered, option => option.Kind == "queue_redistribution" && option.ToCheckpointId == Spare);
    }

    [Fact]
    public void Opening_and_redistribution_take_effect_after_the_decision_without_rewriting_staffing_history()
    {
        var request = Request();
        var open = new AgentOption("open-spare", "checkpoint_open", "Deschide CP-2", Busy, Spare, Count: 1,
            Staff: [new(Spare, 0, 1)], PassengerCount: 20);
        var applied = AgentDecisionInputs.Apply(request, [(10, open)], transferMinutes: 3);
        var schedule = Assert.Single(applied.StaffSchedules!);
        Assert.Equal(Spare, schedule.CheckpointId);
        Assert.Equal([new StaffStep(0, 0), new StaffStep(14, 1)], schedule.Steps);
        Assert.Equal(1, applied.Checkpoints.Single(control => control.CheckpointId == Spare).StaffCount);
        Assert.Equal(1, applied.Checkpoints.Single(control => control.CheckpointId == Busy).StaffCount);
        var move = Assert.Single(applied.Events!, item => item.Type == "queue_redistribution");
        Assert.Equal((Busy, Spare, 20), (move.CheckpointId, move.TargetCheckpointId, move.PassengerCount));
        Assert.Equal(14, move.Minute);
        Assert.True(move.WindowMinutes >= 1);
        Assert.Equal(0, request.Checkpoints.Single(control => control.CheckpointId == Spare).StaffCount);
        Assert.Null(request.StaffSchedules);
        Assert.Null(request.Events);
    }

    [Fact]
    public void A_queue_move_preserves_existing_events_and_replays_with_the_same_event_id()
    {
        var original = new ScheduledEvent(Guid.Parse("90000000-0000-0000-0000-000000000001"), "checkpoint_unavailable", 40,
            CheckpointId: Busy);
        var request = Request() with { Events = [original] };
        var move = new AgentOption("redistribute", "queue_redistribution", "Distribuie coada", Busy, Spare, PassengerCount: 12);
        var applied = AgentDecisionInputs.Apply(request, [(10, move)], transferMinutes: 3);
        Assert.Contains(original, applied.Events!);
        var generated = Assert.Single(applied.Events!, item => item.Type == "queue_redistribution");
        Assert.NotEqual(Guid.Empty, generated.EventId);
        Assert.Equal((Busy, Spare, 12), (generated.CheckpointId, generated.TargetCheckpointId, generated.PassengerCount));
        Assert.Equal(14, generated.Minute);
        Assert.True(generated.WindowMinutes >= 1);
        var replay = AgentDecisionInputs.Apply(request, [(10, move)], transferMinutes: 3);
        Assert.Equal(JsonSerializer.Serialize(applied, Json), JsonSerializer.Serialize(replay, Json));
    }

    private static SimulationRequest Request() => new("1.0", Guid.Parse("90000000-0000-0000-0000-000000000100"),
        Guid.NewGuid(), 1, Guid.NewGuid(), DateTimeOffset.UnixEpoch, "UTC", 60, 42, 100, 2,
        [new(Busy, 1, 1), new(Spare, 1, 0)], Engine: KpiConventions.Engine, SnapshotIntervalMinutes: 1);
}
