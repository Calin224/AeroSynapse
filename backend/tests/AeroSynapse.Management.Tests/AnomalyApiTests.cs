using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class AnomalyApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    // Deliberately sensitive, so the scheduled surge raises at least one alert to review.
    private static readonly AnomalySettings Sensitive = new(0.5, 1, 1, 0);
    private static readonly AnomalySettings Defaults = new(0.95, 10, 6, 10);

    private static async Task<BatchDetails> Batch(ManagementHost host, ScenarioDetails baseline, int[] seeds)
    {
        using var response = await host.Client.PostAsJsonAsync("/simulation-batches", new CreateBatchRequest(Guid.NewGuid(), "DEMO · tipar normal",
            new(baseline.Scenario.Id, baseline.Scenario.Revision), [], seeds, 600), Json, Token);
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var batch = (await response.Content.ReadFromJsonAsync<BatchDetails>(Json, Token))!;
        await StaffingEvaluationTests.Drain(host);
        batch = (await host.Client.GetFromJsonAsync<BatchDetails>($"/simulation-batches/{batch.Batch.Id}", Json, Token))!;
        Assert.Equal("completed", batch.Batch.Status);
        return batch;
    }

    private static async Task<ScenarioDetails> Surge(ManagementHost host, ScenarioDetails scenario)
    {
        var settings = (await host.Client.GetFromJsonAsync<PassengerSettings>($"/scenarios/{scenario.Scenario.Id}/passengers/settings", Json, Token))!;
        using var added = await host.Client.PostAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/events", new EventWriteRequest(scenario.Scenario.Revision,
            new(Guid.NewGuid(), "passenger_surge", scenario.Inputs.StartsAtUtc.AddMinutes(10), settings.Defaults.Batches[0].CheckpointId,
                null, scenario.Inputs.Flights[0].Id, "{\"passengerCount\":40,\"windowMinutes\":5}")), Json, Token);
        Assert.True(added.StatusCode == HttpStatusCode.Created, await added.Content.ReadAsStringAsync(Token));
        return (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{scenario.Scenario.Id}", Json, Token))!;
    }

    private static async Task<Guid> Run(ManagementHost host, ScenarioDetails scenario)
    {
        using var started = await host.Client.PostAsync($"/scenarios/{scenario.Scenario.Id}/runs", null, Token);
        Assert.Equal(HttpStatusCode.Accepted, started.StatusCode);
        var run = (await started.Content.ReadFromJsonAsync<SimulationRun>(Json, Token))!;
        Assert.True(await host.Dispatch(Token));
        return run.Id;
    }

    private static async Task<AnomalyReport> Screen(ManagementHost host, Guid run, Guid batch, AnomalySettings settings)
    {
        using var response = await host.Client.PostAsJsonAsync($"/runs/{run}/anomalies", new AnomalyRequest(batch, settings), Json, Token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Token);
        Assert.Empty(VersionedContract.Validate("AnomalyDetectionResult", payload.GetProperty("detection")));
        return payload.Deserialize<AnomalyReport>(ContractJson.Options)!;
    }

    [Fact]
    public async Task A_surge_run_is_screened_against_real_replications_and_an_analyst_can_confirm_or_reject_its_alert()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var baseline = await StaffingEvaluationTests.Create(host);
        var batch = await Batch(host, baseline, [11, 12, 13, 14, 15, 16]);
        // The batch captured the normal plan; a surge is then scheduled on the same scenario and run.
        var surge = await Surge(host, baseline);
        var run = await Run(host, surge);

        var report = await Screen(host, run, batch.Batch.Id, Sensitive);
        Assert.Equal(6, report.Detection.ReplicationCount); Assert.Empty(report.Excluded);
        Assert.Equal(batch.Replications.Select(item => item.RunId).Order(), report.Detection.ReplicationRuns.Select(item => item.RunId).Order());
        Assert.Equal("aggregate_synthetic", report.Detection.DataScope);
        Assert.Equal(AnomalyOperations.SettingsKey(Sensitive), report.SettingsKey);
        var (row, episode) = report.Detection.Checkpoints.SelectMany(item => item.Episodes.Select(alert => (item, alert))).First();

        var command = new AnomalyReviewRequest(Guid.NewGuid(), batch.Batch.Id, Sensitive, row.CheckpointId, episode.OnsetMinute, "confirmed", "Analist DEMO", "Surge planificat");
        using var confirmed = await host.Client.PostAsJsonAsync($"/runs/{run}/anomalies/reviews", command, Json, Token);
        Assert.True(confirmed.IsSuccessStatusCode, await confirmed.Content.ReadAsStringAsync(Token));
        var first = (await confirmed.Content.ReadFromJsonAsync<AnomalyReviewDetails>(Json, Token))!;
        using var retried = await host.Client.PostAsJsonAsync($"/runs/{run}/anomalies/reviews", command, Json, Token);
        Assert.Equal(first.Id, (await retried.Content.ReadFromJsonAsync<AnomalyReviewDetails>(Json, Token))!.Id);
        using var reused = await host.Client.PostAsJsonAsync($"/runs/{run}/anomalies/reviews", command with { Decision = "rejected" }, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, reused.StatusCode);
        using var changed = await host.Client.PostAsJsonAsync($"/runs/{run}/anomalies/reviews",
            command with { CommandId = Guid.NewGuid(), Decision = "rejected", Reason = "Variație acceptată" }, Json, Token);
        Assert.True(changed.IsSuccessStatusCode, await changed.Content.ReadAsStringAsync(Token));

        var again = await Screen(host, run, batch.Batch.Id, Sensitive);
        Assert.Equal(["confirmed", "rejected"], again.Reviews.Select(item => item.Decision));
        Assert.Empty((await Screen(host, run, batch.Batch.Id, Defaults)).Reviews); // decisions belong to the settings they were made under
        var history = (await host.Client.GetFromJsonAsync<AnomalyReviewDetails[]>($"/runs/{run}/anomalies/reviews", Json, Token))!;
        Assert.Equal(2, history.Length);
        await using var scope = host.Services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<TwinDbContext>().AnomalyReviews.AsNoTracking().Where(item => item.RunId == run).ToArrayAsync(Token);
        Assert.All(stored, item => Assert.True(item.IsSynthetic));
        Assert.All(stored, item => Assert.Contains("\"onsetMinute\"", item.EpisodeJson));

        using var fabricated = await host.Client.PostAsJsonAsync($"/runs/{run}/anomalies/reviews",
            command with { CommandId = Guid.NewGuid(), OnsetMinute = 10079 }, Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, fabricated.StatusCode);
        using var nameless = await host.Client.PostAsJsonAsync($"/runs/{run}/anomalies/reviews", command with { CommandId = Guid.NewGuid(), Actor = " " }, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, nameless.StatusCode);
    }

    [Fact]
    public async Task Too_few_replications_self_inclusion_and_invalid_settings_are_refused()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var baseline = await StaffingEvaluationTests.Create(host);
        var small = await Batch(host, baseline, [1, 2, 3]);
        var run = await Run(host, baseline);
        using var few = await host.Client.PostAsJsonAsync($"/runs/{run}/anomalies", new AnomalyRequest(small.Batch.Id, Defaults), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, few.StatusCode);
        var batch = await Batch(host, baseline, [21, 22, 23, 24, 25, 26]);
        var member = batch.Replications[0].RunId;
        var report = await Screen(host, member, batch.Batch.Id, Defaults);
        Assert.Equal(5, report.Detection.ReplicationCount);
        Assert.Equal("observed_run", Assert.Single(report.Excluded).Reason);
        using var window = await host.Client.PostAsJsonAsync($"/runs/{run}/anomalies", new AnomalyRequest(batch.Batch.Id, Defaults with { WindowMinutes = 2 }), Json, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, window.StatusCode);
        using var missing = await host.Client.PostAsJsonAsync($"/runs/{run}/anomalies", new AnomalyRequest(Guid.NewGuid(), Defaults), Json, Token);
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task Synthetic_test_set_evaluation_is_proxied_and_validated()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        using var response = await host.Client.PostAsJsonAsync("/anomalies/evaluation", new { settings = Defaults }, Json, Token);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync(Token));
        var payload = await response.Content.ReadFromJsonAsync<JsonElement>(Json, Token);
        Assert.Empty(VersionedContract.Validate("AnomalyEvaluationResult", payload));
        Assert.Contains(payload.GetProperty("status").GetString(), new[] { "warming", "ready" });
        using var invalid = await host.Client.PostAsJsonAsync("/anomalies/evaluation", new { settings = Defaults with { MinExceedances = 11 } }, Json, Token);
        Assert.Equal(HttpStatusCode.UnprocessableEntity, invalid.StatusCode);
    }
}
