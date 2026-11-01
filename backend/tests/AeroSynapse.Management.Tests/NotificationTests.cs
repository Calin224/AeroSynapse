using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Operations;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class NotificationTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static async Task<T> Read<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        return (await response.Content.ReadFromJsonAsync<T>(Json, Token))!;
    }
    private static async Task<NotificationReceipt> SendPublish(ManagementHost host, PublishQueueAlertsRequest body) =>
        await Read<NotificationReceipt>(await host.Client.PostAsJsonAsync("/operational-notifications/queue-forecast", body, Json, Token));
    private static async Task<NotificationReceipt> Act(ManagementHost host, Guid id, AlertActionRequest body) =>
        await Read<NotificationReceipt>(await host.Client.PostAsJsonAsync($"/operational-notifications/{id}/actions", body, Json, Token));
    private static async Task<AlertDetails> Detail(ManagementHost host, Guid id) => (await host.Client.GetFromJsonAsync<AlertDetails>($"/operational-notifications/{id}", Json, Token))!;

    [Fact]
    public async Task Real_simulation_forecasts_feed_persistent_audited_comments_assignment_and_ack_without_changing_the_scenario()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var source = await StaffingEvaluationTests.Create(host, scheduledSurge: true);
        var run = await Read<SimulationRun>(await host.Client.PostAsync($"/scenarios/{source.Scenario.Id}/runs", null, Token));
        Assert.True(await host.Dispatch(Token));
        var command = new PublishQueueAlertsRequest(Guid.NewGuid(), source.Scenario.Id, run.Id, 0, 15, 0, 100000);
        var receipt = await SendPublish(host, command);
        Assert.NotEmpty(receipt.AlertIds);
        var id = receipt.AlertIds[0]; var first = await Detail(host, id);
        Assert.Equal("warning", first.Alert.Level); Assert.Equal("queue_forecast", first.Alert.Source);
        Assert.Equal(source.Scenario.Id, first.Alert.ScenarioId); Assert.Equal(run.Id, first.Alert.RunId);
        Assert.True(first.Alert.Evidence.PeakQueuePassengers > 0); Assert.Equal(1, first.Alert.Observations);
        Assert.Equal("captured_snapshot", first.Alert.Evidence.ObservationSource); Assert.NotNull(first.Alert.Evidence.TriggerMinute);
        Assert.Single(first.Activity); Assert.Equal("created", first.Activity[0].Kind);
        Assert.NotNull(first.Activity[0].Evidence); Assert.Equal(first.Alert.Evidence.PeakQueuePassengers, first.Activity[0].Evidence!.PeakQueuePassengers);
        var duplicate = await SendPublish(host, command with { CommandId = Guid.NewGuid() });
        Assert.True(duplicate.Duplicates > 0); Assert.Equal(1, (await Detail(host, id)).Alert.Observations);
        var ack = new AlertActionRequest(Guid.NewGuid(), first.Alert.Revision, "acknowledge", "Supervisor test");
        var accepted = await Act(host, id, ack); var repeated = await Act(host, id, ack);
        Assert.Equal(accepted.CommandId, repeated.CommandId); Assert.Equal(accepted.RecordedAtUtc, repeated.RecordedAtUtc);
        var acknowledged = await Detail(host, id);
        Assert.NotNull(acknowledged.Alert.AcknowledgedAtUtc); Assert.Equal("warning", acknowledged.Alert.Level);
        Assert.Equal(2, acknowledged.Activity.Count); Assert.Equal("acknowledged", acknowledged.Activity[0].Kind);
        await Act(host, id, new(Guid.NewGuid(), acknowledged.Alert.Revision, "assign", "Supervisor test", "Echipa Nord"));
        var assigned = await Detail(host, id); Assert.Equal("Echipa Nord", assigned.Alert.Assignee);
        await Act(host, id, new(Guid.NewGuid(), assigned.Alert.Revision, "comment", "Supervisor test", "Verificăm distribuția personalului."));
        var commented = await Detail(host, id); Assert.Equal(4, commented.Activity.Count);
        Assert.Equal("Verificăm distribuția personalului.", commented.Activity[0].Text);
        using var conflict = await host.Client.PostAsJsonAsync($"/operational-notifications/{id}/actions", ack with { CommandId = Guid.NewGuid() }, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        using var reused = await host.Client.PostAsJsonAsync($"/operational-notifications/{id}/actions", ack with { Actor = "Other" }, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        var list = (await host.Client.GetFromJsonAsync<NotificationPage<OperationalAlert>>($"/operational-notifications?scenarioId={source.Scenario.Id}&acknowledged=true", Json, Token))!;
        Assert.Contains(list.Items, a => a.Id == id);
        var other = (await host.Client.GetFromJsonAsync<NotificationPage<OperationalAlert>>($"/operational-notifications?scenarioId={Guid.NewGuid()}", Json, Token))!; Assert.Empty(other.Items);
        var future = Uri.EscapeDataString(DateTime.UtcNow.AddDays(1).ToString("O"));
        var filtered = (await host.Client.GetFromJsonAsync<NotificationPage<OperationalAlert>>($"/operational-notifications?fromUtc={future}", Json, Token))!; Assert.Empty(filtered.Items);
        var activity = (await host.Client.GetFromJsonAsync<NotificationPage<AlertActivity>>($"/operational-notifications/activity?runId={run.Id}", Json, Token))!;
        Assert.Contains(activity.Items, a => a.Kind == "commented" && a.Actor == "Supervisor test");
        var unchanged = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{source.Scenario.Id}", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(source, Json), JsonSerializer.Serialize(unchanged, Json));
        using var purge = await host.Client.DeleteAsync($"/scenarios/{source.Scenario.Id}?retention=purge&revision={source.Scenario.Revision}", Token);
        Assert.Equal(HttpStatusCode.Conflict, purge.StatusCode);
    }

    [Fact]
    public async Task Completed_runs_alert_on_recorded_queues_over_the_whole_period_for_one_run_a_scenario_or_every_scenario()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var source = await StaffingEvaluationTests.Create(host, scheduledSurge: true);
        var first = await Read<SimulationRun>(await host.Client.PostAsync($"/scenarios/{source.Scenario.Id}/runs", null, Token));
        Assert.True(await host.Dispatch(Token));
        var second = await Read<SimulationRun>(await host.Client.PostAsync($"/scenarios/{source.Scenario.Id}/runs", null, Token));
        Assert.True(await host.Dispatch(Token));
        async Task<NotificationReceipt> Publish(PublishRunAlertsRequest body) =>
            await Read<NotificationReceipt>(await host.Client.PostAsJsonAsync("/operational-notifications/run-queues", body, Json, Token));

        var queued = (await host.Client.GetFromJsonAsync<KpiResult>($"/runs/{first.Id}/kpis?limit=50000", Json, Token))!
            .Snapshots.Where(s => s.QueuedPassengers > 0).ToArray();
        Assert.NotEmpty(queued);
        var single = await Publish(new(Guid.NewGuid(), source.Scenario.Id, first.Id, 0, 100000));
        Assert.Equal(queued.Select(s => s.ZoneId).Distinct().Count(), single.AlertIds.Count);
        var alert = (await Detail(host, single.AlertIds[0])).Alert;
        var zone = queued.Where(s => s.ZoneId == alert.ZoneId).ToArray();
        Assert.Equal(("warning", "recorded_queue", 0, first.Id), (alert.Level, alert.Source, alert.HorizonMinutes, alert.RunId));
        Assert.Equal((zone.Min(s => s.Minute), zone.Max(s => s.Minute), (double)zone.Max(s => s.QueuedPassengers)),
            (alert.Evidence.FromMinute, alert.Evidence.ToMinute, alert.Evidence.PeakQueuePassengers));

        var scenario = await Publish(new(Guid.NewGuid(), source.Scenario.Id, null, 0, 100000));
        Assert.Equal(single.AlertIds.Count, scenario.Duplicates);
        Assert.Contains(scenario.AlertIds, id => !single.AlertIds.Contains(id));
        var everyCommand = new PublishRunAlertsRequest(Guid.NewGuid(), null, null, 0, 100000);
        var every = await Publish(everyCommand); var replay = await Publish(everyCommand);
        Assert.Superset(scenario.AlertIds.ToHashSet(), every.AlertIds.ToHashSet());
        Assert.Equal(every.AlertIds, replay.AlertIds);
        var listed = (await host.Client.GetFromJsonAsync<NotificationPage<OperationalAlert>>($"/operational-notifications?scenarioId={source.Scenario.Id}&limit=100", Json, Token))!;
        Assert.Equal(new[] { first.Id, second.Id }.Order(), listed.Items.Select(a => a.RunId).Distinct().Order());

        using var foreign = await host.Client.PostAsJsonAsync("/operational-notifications/run-queues",
            new PublishRunAlertsRequest(Guid.NewGuid(), Guid.NewGuid(), first.Id, 0, 100000), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        using var invalid = await host.Client.PostAsJsonAsync("/operational-notifications/run-queues",
            new PublishRunAlertsRequest(Guid.NewGuid(), null, first.Id, 0, 100000), Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    private sealed class LiveInspector : IIntelligenceClient
    {
        public EngineExecution? Current { get; set; }
        public Task<EngineExecution> StartAsync(SimulationRequest request, string correlationId, CancellationToken token) => throw new InvalidOperationException();
        public Task<EngineExecution?> GetAsync(Guid id, string correlationId, CancellationToken token) => Task.FromResult(Current);
    }

    [Fact]
    public async Task Live_forecast_levels_escalate_resolve_reopen_and_deduplicate_concurrent_requests_without_stale_regression()
    {
        var inspector = new LiveInspector(); await using var host = await ManagementHost.CreateAsync(client: inspector);
        var source = await StaffingEvaluationTests.Create(host);
        await using var scope = host.Services.CreateAsyncScope();
        var storage = scope.ServiceProvider.GetRequiredService<IRunStorageService>();
        var run = await scope.ServiceProvider.GetRequiredService<IScenarioService>().CreateRunAsync(source.Scenario.Id, Token);
        run = await storage.TransitionAsync(run.Id, new("running", run.Revision), Token);
        var captured = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson!, Json)!;
        var simulation = CapturedSimulation.Request(run.Id, captured); var target = simulation.PassengerGeneration!.Flights[0].CheckpointId;
        void Frame(int minute, int queue) => inspector.Current = new(run.Id, "running", null,
            new(minute, simulation.Checkpoints.Select(cp => new ProgressZone(cp.CheckpointId, cp.CheckpointId == target ? queue : 0,
                cp.CheckpointId == target ? simulation.PassengerCount - queue : 0, 0)).ToArray()));
        var command = new PublishQueueAlertsRequest(Guid.NewGuid(), source.Scenario.Id, run.Id, 0, 15, 1, 3, true);
        Frame(10, simulation.PassengerCount);
        var calibration = await Read<QueueForecastResult>(await host.Client.PostAsJsonAsync($"/runs/{run.Id}/queue-forecast/live", new QueueForecastLiveOptions(15, null), Json, Token));
        var capacity = calibration.Checkpoints.Single(c => c.CheckpointId == target).Points[0].Capacity;
        Assert.True(simulation.PassengerCount > capacity + 3, $"Population {simulation.PassengerCount}, capacity {capacity}");
        command = command with { WarningThreshold = 0, CriticalThreshold = 2 };
        Frame(10, (int)Math.Ceiling(capacity) + 1);
        var replies = await Task.WhenAll(SendPublish(host, command), SendPublish(host, command));
        Assert.Equal(replies[0].RecordedAtUtc, replies[1].RecordedAtUtc); Assert.Equal(replies[0].AlertIds, replies[1].AlertIds);
        var id = replies[0].AlertIds.Single(); var alert = await Detail(host, id); Assert.Equal("warning", alert.Alert.Level);
        await Act(host, id, new(Guid.NewGuid(), alert.Alert.Revision, "acknowledge", "Supervisor"));
        Frame(20, simulation.PassengerCount); await SendPublish(host, command with { CommandId = Guid.NewGuid() });
        alert = await Detail(host, id); Assert.Equal("critical", alert.Alert.Level); Assert.Null(alert.Alert.AcknowledgedAtUtc);
        Assert.Equal(2, alert.Alert.Observations);
        Frame(15, 0); var stale = await SendPublish(host, command with { CommandId = Guid.NewGuid() });
        Assert.Equal(1, stale.StaleObservations); Assert.Equal("critical", (await Detail(host, id)).Alert.Level);
        Frame(35, 0); await SendPublish(host, command with { CommandId = Guid.NewGuid() });
        alert = await Detail(host, id); Assert.Equal("resolved", alert.Alert.Level); Assert.Equal(3, alert.Alert.Observations);
        using var invalid = await host.Client.PostAsJsonAsync($"/operational-notifications/{id}/actions", new AlertActionRequest(Guid.NewGuid(), alert.Alert.Revision, "acknowledge", "Supervisor"), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, invalid.StatusCode);
        Frame(40, simulation.PassengerCount); await SendPublish(host, command with { CommandId = Guid.NewGuid() });
        alert = await Detail(host, id); Assert.Equal("critical", alert.Alert.Level); Assert.Equal("reopened", alert.Activity[0].Kind);
        Assert.Equal("running", (await storage.GetAsync(run.Id, Token))!.Status);
        Assert.Empty((await storage.SnapshotsAsync(run.Id, new(IncludeInvalid: true), Token)).Items);
    }

    [Fact]
    public async Task Invalid_filters_non_synthetic_sources_and_foreign_context_are_rejected()
    {
        await using var host = await ManagementHost.CreateAsync(); var source = await StaffingEvaluationTests.Create(host);
        var run = await Read<SimulationRun>(await host.Client.PostAsync($"/scenarios/{source.Scenario.Id}/runs", null, Token)); Assert.True(await host.Dispatch(Token));
        var command = new PublishQueueAlertsRequest(Guid.NewGuid(), source.Scenario.Id, run.Id, 0, 15, 2, 2);
        using var invalid = await host.Client.PostAsJsonAsync("/operational-notifications/queue-forecast", command, Json, Token); Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        using var filter = await host.Client.GetAsync("/operational-notifications?limit=0", Token); Assert.Equal(HttpStatusCode.BadRequest, filter.StatusCode);
        var other = await Read<ScenarioDetails>(await host.Client.PostAsJsonAsync($"/scenarios/{source.Scenario.Id}/duplicate", new DuplicateScenarioRequest("FOREIGN-TEST", "Other synthetic source"), Json, Token));
        using var foreign = await host.Client.PostAsJsonAsync("/operational-notifications/queue-forecast", command with { ScenarioId = other.Scenario.Id, CriticalThreshold = 4 }, Json, Token); Assert.Equal(HttpStatusCode.Conflict, foreign.StatusCode);
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        await db.Scenarios.Where(s => s.Id == source.Scenario.Id).ExecuteUpdateAsync(s => s.SetProperty(r => r.IsSynthetic, false), Token);
        using var nonsynthetic = await host.Client.PostAsJsonAsync("/operational-notifications/queue-forecast", command with { CriticalThreshold = 4 }, Json, Token); Assert.Equal(HttpStatusCode.Conflict, nonsynthetic.StatusCode);
        Assert.Empty(await db.NotificationActivity.ToArrayAsync(Token));
    }
}
