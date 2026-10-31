using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Execution;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class ReplayTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static Task<SimulationRun> Run(ManagementHost host, Guid scenarioId, bool dispatch = true) => host.StartRunAsync(scenarioId, dispatch);
    private static Task<ReplayEnvelope?> Read(ManagementHost host, Guid id) => host.Client.GetFromJsonAsync<ReplayEnvelope>($"/runs/{id}/replay", Json, Token);
    private static BookmarkWriteRequest Bookmark(ScenarioDetails scenario) => new(5.25, "Coadă observată", scenario.Inputs.StaffAllocations[0].CheckpointId, "Notă operațională DEMO");

    [Fact]
    public async Task Real_engine_replay_is_a_stable_complete_DB_snapshot_after_scenario_edits_and_archival()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true); var scenario = await host.CreateScenarioAsync();
        var run = await Run(host, scenario.Scenario.Id);
        var first = (await Read(host, run.Id))!; var repeated = (await Read(host, run.Id))!;
        Assert.Equal("1.0", first.ContractVersion); Assert.Equal(run.Id, first.RunId);
        Assert.Equal(first.PayloadJson, repeated.PayloadJson); Assert.Equal(first.ContentHash, repeated.ContentHash);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(first.PayloadJson))), first.ContentHash);
        var data = JsonSerializer.Deserialize<ReplayData>(first.PayloadJson, Json)!;
        Assert.Equal(run.Id, data.Run.Id); Assert.Equal(1, data.IntervalMinutes); Assert.Equal(183, data.Samples.Count);
        var kpis = (await host.Client.GetFromJsonAsync<KpiResult>($"/runs/{run.Id}/kpis?limit=50000", Json, Token))!;
        Assert.Equal(kpis.Snapshots.Count, data.Samples.Count);
        foreach (var row in data.Samples)
        {
            Assert.NotNull(row.Kpi); Assert.Equal(run.Id, row.Kpi.RunId); Assert.Equal(row.ZoneId, row.Kpi.ZoneId);
            Assert.Equal(row.Minute, row.Kpi.ElapsedMinutes); Assert.Equal(row.QueuedPassengers, row.Kpi.QueuedPassengers);
            Assert.Equal(kpis.Snapshots.Single(k => k.ZoneId == row.ZoneId && k.Minute == row.Minute), row.Kpi);
        }
        Assert.Equal(scenario.Inputs.Events, data.Captured.Scenario.Inputs.Events);
        using var edit = await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}", new ScenarioWriteRequest(scenario.Scenario.Code,
            "Editat după captură", scenario.Scenario.Description, scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId,
            scenario.Inputs with { Seed = 123, Events = [] }, 1), Json, Token);
        Assert.Equal(HttpStatusCode.OK, edit.StatusCode);
        Assert.Equal(first.ContentHash, (await Read(host, run.Id))!.ContentHash);
        using var archive = await host.Client.DeleteAsync($"/scenarios/{scenario.Scenario.Id}?retention=archive&revision=2", Token);
        Assert.Equal(HttpStatusCode.OK, archive.StatusCode); Assert.Equal(first.PayloadJson, (await Read(host, run.Id))!.PayloadJson);
    }

    [Fact]
    public async Task Bookmarks_are_revisioned_idempotent_run_scoped_annotations_and_do_not_change_the_replay_hash()
    {
        await using var host = await ManagementHost.CreateAsync(); var scenario = await host.CreateScenarioAsync(); var run = await Run(host, scenario.Scenario.Id);
        host.Engine.Behavior = (_, _) => throw new InvalidOperationException("Replay must never execute the engine.");
        var before = (await Read(host, run.Id))!; var id = Guid.NewGuid(); var path = $"/runs/{run.Id}/bookmarks/{id}"; var request = Bookmark(scenario);
        async Task<ReplayBookmark> Save(BookmarkWriteRequest value)
        {
            using var response = await host.Client.PutAsJsonAsync(path, value, Json, Token);
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Token));
            return (await response.Content.ReadFromJsonAsync<ReplayBookmark>(Json, Token))!;
        }
        var first = await Save(request); var duplicate = await Save(request);
        Assert.Equal(first, duplicate); Assert.Equal(1, first.Revision); Assert.Equal(5.25, first.Minute);
        var edited = await Save(request with { Label = "Actualizat", Minute = 7.5, Revision = 1 });
        Assert.Equal(2, edited.Revision);
        using var stale = await host.Client.PutAsJsonAsync(path, request with { Revision = 1 }, Json, Token); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var after = (await Read(host, run.Id))!;
        Assert.Equal(before.PayloadJson, after.PayloadJson); Assert.Equal(before.ContentHash, after.ContentHash);
        Assert.Equal(edited, Assert.Single(after.Bookmarks));
        Assert.Equal(1, (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{scenario.Scenario.Id}", Json, Token))!.Scenario.Revision);
        using var blockedDelete = await host.Client.DeleteAsync(path + "?revision=1", Token); Assert.Equal(HttpStatusCode.Conflict, blockedDelete.StatusCode);
        using var delete = await host.Client.DeleteAsync(path + "?revision=2", Token); Assert.Equal(HttpStatusCode.NoContent, delete.StatusCode);
        using var again = await host.Client.DeleteAsync(path + "?revision=2", Token); Assert.Equal(HttpStatusCode.NoContent, again.StatusCode);
        Assert.Empty((await Read(host, run.Id))!.Bookmarks);
    }

    [Fact]
    public async Task Bookmarks_mark_the_check_in_desks_only_in_runs_that_simulated_them()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var desks = SyntheticScenarioSeeder.CheckInAreaId;
        var plain = await host.CreateScenarioAsync();
        using var created = await host.Client.PostAsJsonAsync("/scenarios", new ScenarioWriteRequest("CHECK-IN-REPLAY", "Cu ghișee", "", plain.Scenario.SceneId,
            plain.Scenario.LayoutVersionId, plain.Inputs with { CheckIn = new(desks, 4, .5m) }), Json, Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        var simulated = (await created.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        async Task<HttpResponseMessage> Mark(Guid scenarioId)
        {
            var run = await Run(host, scenarioId);
            return await host.Client.PutAsJsonAsync($"/runs/{run.Id}/bookmarks/{Guid.NewGuid()}",
                new BookmarkWriteRequest(5, "Coadă la ghișee", desks, ""), Json, Token);
        }
        using var accepted = await Mark(simulated.Scenario.Id);
        Assert.True(accepted.StatusCode == HttpStatusCode.OK, await accepted.Content.ReadAsStringAsync(Token));
        Assert.Equal(desks, (await accepted.Content.ReadFromJsonAsync<ReplayBookmark>(Json, Token))!.ZoneId);
        using var rejected = await Mark(plain.Scenario.Id);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
    }

    [Theory]
    [InlineData("negative")][InlineData("horizon")][InlineData("label")][InlineData("long-label")][InlineData("nul")]
    [InlineData("note")][InlineData("zone")][InlineData("revision")][InlineData("id")]
    public async Task Invalid_bookmarks_are_explicit_and_never_written(string kind)
    {
        await using var host = await ManagementHost.CreateAsync(); var scenario = await host.CreateScenarioAsync(); var run = await Run(host, scenario.Scenario.Id);
        var request = Bookmark(scenario);
        request = kind switch { "negative" => request with { Minute = -1 }, "horizon" => request with { Minute = 61 },
            "label" => request with { Label = "" }, "long-label" => request with { Label = new string('a',121) }, "nul" => request with { Note = "bad\0note" },
            "note" => request with { Note = new string('n',501) }, "zone" => request with { ZoneId = Guid.NewGuid() }, "revision" => request with { Revision = -1 }, _ => request };
        using var invalid = await host.Client.PutAsJsonAsync($"/runs/{run.Id}/bookmarks/{(kind == "id" ? Guid.Empty : Guid.NewGuid())}", request, Json, Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode); Assert.Contains("validation_failed", await invalid.Content.ReadAsStringAsync(Token));
        Assert.Empty((await host.Client.GetFromJsonAsync<ReplayBookmark[]>($"/runs/{run.Id}/bookmarks", Json, Token))!);
    }

    [Fact]
    public async Task Pending_failed_missing_and_historical_runs_never_offer_valid_replay_data()
    {
        await using var host = await ManagementHost.CreateAsync(); var scenario = await host.CreateScenarioAsync(); var run = await Run(host, scenario.Scenario.Id, false);
        using var pending = await host.Client.GetAsync($"/runs/{run.Id}/replay", Token); Assert.Equal(HttpStatusCode.Conflict, pending.StatusCode);
        host.Engine.Behavior = (_, _) => throw new InvalidOperationException("Deliberate demo failure"); Assert.True(await host.Dispatch(Token));
        using var failed = await host.Client.GetAsync($"/runs/{run.Id}/replay", Token); Assert.Equal(HttpStatusCode.Conflict, failed.StatusCode);
        using var write = await host.Client.PutAsJsonAsync($"/runs/{run.Id}/bookmarks/{Guid.NewGuid()}", Bookmark(scenario), Json, Token); Assert.Equal(HttpStatusCode.Conflict, write.StatusCode);
        using var missing = await host.Client.GetAsync($"/runs/{Guid.NewGuid()}/replay", Token); Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        var historical = await db.Runs.Where(r => r.ScenarioId == null).Select(r => r.Id).FirstAsync(Token);
        using var old = await host.Client.GetAsync($"/runs/{historical}/replay", Token); Assert.Equal(HttpStatusCode.Conflict, old.StatusCode);
    }

    [Fact]
    public async Task Switching_runs_does_not_expose_or_overwrite_foreign_bookmarks_and_purge_removes_annotations()
    {
        await using var host = await ManagementHost.CreateAsync(); var scenario = await host.CreateScenarioAsync();
        var a = await Run(host,scenario.Scenario.Id); var b = await Run(host,scenario.Scenario.Id); var id = Guid.NewGuid();
        using var saved = await host.Client.PutAsJsonAsync($"/runs/{a.Id}/bookmarks/{id}", Bookmark(scenario), Json, Token); Assert.Equal(HttpStatusCode.OK, saved.StatusCode);
        Assert.Empty((await Read(host,b.Id))!.Bookmarks);
        using var overwrite = await host.Client.PutAsJsonAsync($"/runs/{b.Id}/bookmarks/{id}", Bookmark(scenario), Json, Token); Assert.Equal(HttpStatusCode.Conflict,overwrite.StatusCode);
        Assert.Single((await Read(host,a.Id))!.Bookmarks);
        using var archive = await host.Client.DeleteAsync($"/scenarios/{scenario.Scenario.Id}?retention=archive&revision=1",Token); Assert.Equal(HttpStatusCode.OK,archive.StatusCode);
        Assert.Single((await Read(host,a.Id))!.Bookmarks);
        using var purge = await host.Client.DeleteAsync($"/scenarios/{scenario.Scenario.Id}?retention=purge&revision=2",Token); Assert.Equal(HttpStatusCode.OK,purge.StatusCode);
        await using var scope = host.Services.CreateAsyncScope(); var db = scope.ServiceProvider.GetRequiredService<TwinDbContext>();
        Assert.False(await db.Events.AnyAsync(e => e.Id == id,Token)); Assert.False(await db.Runs.AnyAsync(r => r.Id == a.Id,Token));
    }
}
