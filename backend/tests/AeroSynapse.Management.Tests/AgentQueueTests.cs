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

    private static AgentCheckpointState Control(Guid id, int staff, int queued, bool available = true) =>
        new(id, staff, staff, available, 1, queued, 0, 0, 0, null, 0, 0, null);

    private static IReadOnlyList<AgentOption> Offered(AgentCheckpointState[] controls, int reserve = 0, params Guid[] frozen) =>
        AgentCandidates.QueueActions(controls, Agent.TransferMinutes, frozen.ToHashSet(), Names, reserve).ToArray();

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
}
