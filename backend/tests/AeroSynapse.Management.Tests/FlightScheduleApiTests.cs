using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class FlightScheduleApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static Task<ScenarioDetails> Create(ManagementHost host) =>
        host.CreateScenarioAsync(request => request with { Inputs = request.Inputs with { DurationMinutes = 90 } });
    private static async Task<RunInputSnapshot> Capture(ManagementHost host, Guid id) =>
        JsonSerializer.Deserialize<RunInputSnapshot>((await host.StartRunAsync(id, dispatch: false)).InputSnapshotJson!, Json)!;

    [Fact]
    public async Task Edited_flight_and_gate_are_persisted_in_the_next_snapshot_while_the_previous_snapshot_stays_unchanged()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Create(host);
        var id = scenario.Scenario.Id;
        var before = await Capture(host, id);
        var schedule = (await host.Client.GetFromJsonAsync<FlightSchedule>($"/scenarios/{id}/flight-schedule", Json, Token))!;
        Assert.True(schedule.IsSynthetic); Assert.Equal(5, schedule.Gates.Count);
        Assert.All(schedule.Gates, gate => Assert.True(gate.IsSynthetic));
        var flight = schedule.Flights[0] with { GateId = SyntheticScenarioSeeder.Id(202), DelayMinutes = 5, Status = "delayed", PassengerCapacity = 140 };
        using var response = await host.Client.PutAsJsonAsync($"/scenarios/{id}/flights/{flight.Id}", new FlightWriteRequest(1, flight), Json, Token);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var after = await Capture(host, id);
        Assert.Equal(2, after.Scenario.Scenario.Revision); Assert.Equal(flight, after.Scenario.Inputs.Flights[0]);
        Assert.Equal(SyntheticScenarioSeeder.Id(401), after.Scenario.Inputs.Flows[0].RouteId);
        Assert.Equal(SyntheticScenarioSeeder.Id(201), before.Scenario.Inputs.Flights[0].GateId);
        Assert.Equal(0, before.Scenario.Inputs.Flights[0].DelayMinutes);
        var reloaded = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        Assert.Equal(flight, reloaded.Inputs.Flights[0]); Assert.True(reloaded.Scenario.IsSynthetic);
    }

    [Fact]
    public async Task Csv_import_supports_five_flights_and_is_atomic_for_overlap_invalid_rows_and_stale_revisions()
    {
        await using var host = await ManagementHost.CreateAsync();
        var scenario = await Create(host);
        var id = scenario.Scenario.Id;
        var schedule = (await host.Client.GetFromJsonAsync<FlightSchedule>($"/scenarios/{id}/flight-schedule", Json, Token))!;
        var start = scenario.Inputs.StartsAtUtc;
        schedule = schedule with { Flights = [schedule.Flights[0], .. Enumerable.Range(2, 4).Select(number =>
            new ScenarioFlight(Guid.NewGuid(), $"DEMO-{number}", SyntheticScenarioSeeder.Id(200 + number), start.AddMinutes(50 + number * 5), 150,
                start.AddMinutes(20 + number * 5), start.AddMinutes(45 + number * 5)))] };
        var csv = FlightScheduleCsv.Export(schedule);
        using var imported = await host.Client.PostAsJsonAsync($"/scenarios/{id}/flights/import", new FlightCsvImportRequest(1, csv), Json, Token);
        Assert.Equal(HttpStatusCode.OK, imported.StatusCode);
        var saved = (await imported.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        Assert.Equal(5, saved.Inputs.Flights.Count); Assert.Equal(2, saved.Scenario.Revision);
        saved = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
        using var export = await host.Client.GetAsync($"/scenarios/{id}/flights/export", Token);
        Assert.Equal("text/csv", export.Content.Headers.ContentType!.MediaType);
        Assert.Contains("demo-flights", export.Content.Headers.ContentDisposition!.FileName!);
        Assert.Equal(saved.Inputs.Flights, FlightScheduleCsv.Import(await export.Content.ReadAsStringAsync(Token), schedule.Gates));
        foreach (var invalid in new[] { csv.Replace("G-2", "G-1"), csv.Replace("\"true\"", "\"false\""), csv.Replace("\"150\"", "\"150.5\"") })
        {
            using var failed = await host.Client.PostAsJsonAsync($"/scenarios/{id}/flights/import", new FlightCsvImportRequest(2, invalid), Json, Token);
            Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
            var unchanged = (await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{id}", Json, Token))!;
            Assert.Equal(JsonSerializer.Serialize(saved, Json), JsonSerializer.Serialize(unchanged, Json));
        }
        using var stale = await host.Client.PostAsJsonAsync($"/scenarios/{id}/flights/import", new FlightCsvImportRequest(1, csv), Json, Token);
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var captured = await Capture(host, id);
        Assert.Equal(5, captured.Scenario.Inputs.Flights.Count);
    }
}
