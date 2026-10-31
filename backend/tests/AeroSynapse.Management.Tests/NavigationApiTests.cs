using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.Airports;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class NavigationApiTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static NavigationConfig Config()
    {
        using var json=JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"navigation","preview-request.json")));
        var input=ContractJson.Read<NavigationPreviewRequest>(json.RootElement,"NavigationPreviewRequest").Navigation;
        return new(input.NavigationVersion,input.Graph,input.CheckpointBindings,input.GateBindings,input.AccessProfiles,input.ClosedZoneIds,input.ClosedEdgeIds);
    }
    private static async Task<ScenarioDetails> Create(ManagementHost host,bool passengerProfiles=true)
    {
        using var published=await host.Client.PostAsync($"/scenes/{SyntheticScenarioSeeder.SceneId}/layout-versions",null,Token);
        var layout=(await published.Content.ReadFromJsonAsync<PublishedLayout>(Json,Token))!;
        var request=JsonSerializer.Deserialize<ScenarioWriteRequest>(File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"scenario-request.json")),Json)!;
        var start=request.Inputs.StartsAtUtc;
        var flight=request.Inputs.Flights[0] with { PassengerCapacity=2,DepartureAtUtc=start.AddMinutes(10),BoardingOpensAtUtc=start.AddMinutes(1),BoardingClosesAtUtc=start.AddMinutes(3) };
        request=request with { LayoutVersionId=layout.Version.Id,Inputs=request.Inputs with { DurationMinutes=10,Flights=[flight],
            Populations=[request.Inputs.Populations[0] with { Count=2 }],Flows=[request.Inputs.Flows[0] with { PassengerCount=2,EndsAtUtc=start.AddMinutes(3) }],
            StaffAllocations=request.Inputs.StaffAllocations.Take(1).ToArray(),Events=[] } };
        using var created=await host.Client.PostAsJsonAsync("/scenarios",request,Json,Token); Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        var scenario=(await created.Content.ReadFromJsonAsync<ScenarioDetails>(Json,Token))!;
        if (!passengerProfiles) return scenario;
        var settings=(await host.Client.GetFromJsonAsync<PassengerSettings>($"/scenarios/{scenario.Scenario.Id}/passengers/settings",Json,Token))!;
        var batch=settings.Defaults.Batches[0] with { Arrival=new("constant",0,0,0,0,0),Service=new("constant",7,7,7,0,7),
            Travel=new("constant",1,1,1,0,1),Groups=[new("solo",1,1,1,1,1)] };
        using var configured=await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/passengers/settings",
            new PassengerConfigWriteRequest(scenario.Scenario.Revision,42,new("1.0",[batch])),Json,Token);
        Assert.Equal(HttpStatusCode.OK,configured.StatusCode);
        return (await configured.Content.ReadFromJsonAsync<ScenarioDetails>(Json,Token))!;
    }
    private static async Task<ScenarioDetails> Save(ManagementHost host,ScenarioDetails scenario,NavigationConfig config)
    {
        using var saved=await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/navigation/settings",new NavigationWriteRequest(scenario.Scenario.Revision,config),Json,Token);
        Assert.Equal(HttpStatusCode.OK,saved.StatusCode); return (await saved.Content.ReadFromJsonAsync<ScenarioDetails>(Json,Token))!;
    }
    private static async Task<(Guid Id,NavigationResult Result)> Run(ManagementHost host,Guid scenario)
    {
        using var started=await host.Client.PostAsync($"/scenarios/{scenario}/runs",null,Token); Assert.Equal(HttpStatusCode.Accepted,started.StatusCode);
        var run=(await started.Content.ReadFromJsonAsync<SimulationRun>(Json,Token))!;
        Assert.True(await host.Dispatch(Token));
        var status=(await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}",Json,Token))!;
        Assert.True(status.Status == "completed", $"Run {run.Id}: {status.Status}; {status.FailureReason}; {JsonSerializer.Serialize(status.Error, Json)}");
        using var response=await host.Client.GetAsync($"/runs/{run.Id}/navigation?limit=100000",Token); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        return (run.Id,(await response.Content.ReadFromJsonAsync<NavigationResult>(Json,Token))!);
    }
    [Fact]
    public async Task Real_graph_and_queue_engine_replans_closed_corridors_and_preserves_historical_boarding_assessments()
    {
        await using var host=await ManagementHost.CreateAsync(realEngine:true);
        var scenario=await Create(host); var config=Config(); scenario=await Save(host,scenario,config); var id=scenario.Scenario.Id;
        var preview=(await host.Client.GetFromJsonAsync<NavigationResult>($"/scenarios/{id}/navigation?revision={scenario.Scenario.Revision}&limit=100000",Json,Token))!;
        Assert.Equal(2,preview.RoutedCount); Assert.False(preview.Assessed); Assert.All(preview.Journeys,item => Assert.Equal(2,item.WalkingMinutes));
        using var export=await host.Client.GetAsync($"/scenarios/{id}/navigation/export?revision={scenario.Scenario.Revision}",Token);
        Assert.Equal(HttpStatusCode.NotFound,export.StatusCode);
        var baseline=await Run(host,id);
        var processes=(await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{baseline.Id}/processes",Json,Token))!;
        Assert.Equal(2,processes.AtGateCount); Assert.Equal(1,processes.DeadlineMissedCount);
        Assert.Equal(new[] { 1d,2d },processes.Passengers.Select(person => person.SecurityClearedMinute!.Value));
        Assert.Equal(1,baseline.Result.Flights[0].SafeCount); Assert.Equal(1,baseline.Result.Flights[0].LateCount);
        Assert.Equal(new[] { 3d,4d },baseline.Result.Journeys.Select(item => item.GateArrivalMinute!.Value));
        var corridor=config.Graph.Nodes.Single(item => item.Name=="Coridor principal").Id;
        scenario=await Save(host,scenario,config with { ClosedZoneIds=[corridor] });
        var changed=await Run(host,id);
        Assert.Equal(baseline.Result.PopulationFingerprint,changed.Result.PopulationFingerprint);
        Assert.NotEqual(baseline.Result.GraphFingerprint,changed.Result.GraphFingerprint);
        Assert.Equal(2,changed.Result.Flights[0].LateCount);
        Assert.All(changed.Result.Routes,route => { Assert.DoesNotContain(corridor,route.ZoneIds); Assert.Equal(3,route.BaseWalkingMinutes); });
        var preserved=(await host.Client.GetFromJsonAsync<NavigationResult>($"/runs/{baseline.Id}/navigation?limit=100000",Json,Token))!;
        Assert.Equal(baseline.Result.GraphFingerprint,preserved.GraphFingerprint); Assert.Equal(baseline.Result.Journeys,preserved.Journeys);
        scenario=await Save(host,scenario,config with { ClosedZoneIds=config.Graph.Nodes.Where(node => node.Kind is "Walkway" or "Stairs").Select(node => node.Id).ToArray() });
        var unavailable=await Run(host,id);
        Assert.Equal(2,unavailable.Result.ErrorCount); Assert.Equal(0,unavailable.Result.RoutedCount);
        Assert.All(unavailable.Result.Journeys,person => { Assert.Equal("route_unreachable",person.Error!.Code); Assert.Equal("unreachable",person.BoardingRisk); });
        var page=(await host.Client.GetFromJsonAsync<NavigationResult>($"/runs/{baseline.Id}/navigation?offset=1&limit=1",Json,Token))!;
        Assert.Single(page.Journeys); Assert.Equal(baseline.Result.Journeys[1],page.Journeys[0]); Assert.Equal(2,page.ModeledCount);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SimPy_reports_closed_and_overloaded_controls_without_allowing_security_bypass(bool closed)
    {
        await using var host=await ManagementHost.CreateAsync(realEngine:true);
        var scenario=await Create(host);
        if (closed)
        {
            using var update=await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}",
                new ScenarioWriteRequest(scenario.Scenario.Code,scenario.Scenario.Name,scenario.Scenario.Description,scenario.Scenario.SceneId,
                    scenario.Scenario.LayoutVersionId,scenario.Inputs with { StaffAllocations=[] },scenario.Scenario.Revision),Json,Token);
            Assert.Equal(HttpStatusCode.OK,update.StatusCode);
            scenario=(await update.Content.ReadFromJsonAsync<ScenarioDetails>(Json,Token))!;
        }
        else
        {
            var config=scenario.Inputs.PassengerGeneration!;
            var batch=config.Batches[0] with { Service=new("constant",20,20,20,0,20),Groups=[new("solo",1,1,1,10,1)] };
            using var update=await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/passengers/settings",
                new PassengerConfigWriteRequest(scenario.Scenario.Revision,42,config with { Batches=[batch] }),Json,Token);
            Assert.Equal(HttpStatusCode.OK,update.StatusCode);
            scenario=(await update.Content.ReadFromJsonAsync<ScenarioDetails>(Json,Token))!;
        }
        scenario=await Save(host,scenario,Config());
        var run=await Run(host,scenario.Scenario.Id);
        var report=(await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{run.Id}/processes?limit=100",Json,Token))!;
        Assert.Equal(closed ? 2 : 1,report.QueuedCount); Assert.Equal(closed ? 0 : 1,report.ProcessingCount);
        Assert.Equal(0,report.AtGateCount); Assert.Equal(2,report.DeadlineMissedCount);
        Assert.All(report.Passengers,person => { Assert.Null(person.SecurityClearedMinute); Assert.Null(person.GateArrivalMinute); });
        Assert.All(report.Checkpoints,cp => Assert.True(cp.PeakBusy<=cp.Capacity));
        var page=(await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{run.Id}/processes?offset=1&limit=1",Json,Token))!;
        Assert.Single(page.Passengers); Assert.Equal(report.Passengers[1],page.Passengers[0]); Assert.Equal(2,page.ArrivedCount);
    }
    [Fact]
    public async Task Empty_scenario_completes_ninety_virtual_minutes_and_persists_an_empty_process_report()
    {
        await using var host=await ManagementHost.CreateAsync(realEngine:true);
        var scenario=await Create(host,false);
        using var update=await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}",
            new ScenarioWriteRequest(scenario.Scenario.Code,scenario.Scenario.Name,scenario.Scenario.Description,scenario.Scenario.SceneId,
                scenario.Scenario.LayoutVersionId,scenario.Inputs with { DurationMinutes=90,Flights=[],Populations=[],Flows=[] },scenario.Scenario.Revision),Json,Token);
        Assert.Equal(HttpStatusCode.OK,update.StatusCode);
        using var started=await host.Client.PostAsync($"/scenarios/{scenario.Scenario.Id}/runs",null,Token);
        Assert.Equal(HttpStatusCode.Accepted,started.StatusCode);
        var run=(await started.Content.ReadFromJsonAsync<SimulationRun>(Json,Token))!;
        Assert.True(await host.Dispatch(Token));
        var status=(await host.Client.GetFromJsonAsync<RunDetails>($"/runs/{run.Id}",Json,Token))!;
        Assert.True(status.Status=="completed",status.FailureReason);
        var report=(await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{run.Id}/processes",Json,Token))!;
        Assert.Equal(90,report.HorizonMinutes); Assert.Equal(0,report.PopulationCount); Assert.Empty(report.Passengers);
        Assert.All(report.Checkpoints,cp => Assert.Equal(0,cp.ProcessedCount));
        Assert.Contains(KpiConventions.Engine,status.InputSnapshotJson!);
    }
    [Fact]
    public async Task Scheduled_events_execute_through_the_real_engine_and_preserve_earlier_run_snapshots()
    {
        await using var host = await ManagementHost.CreateAsync(realEngine: true);
        var scenario = await Save(host, await Create(host), Config());
        var baseline = await Run(host, scenario.Scenario.Id);
        var baselineReport = (await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{baseline.Id}/processes", Json, Token))!;
        var baselineKpis = (await host.Client.GetFromJsonAsync<KpiResult>($"/runs/{baseline.Id}/kpis?limit=50000", Json, Token))!;
        var control = scenario.Inputs.PassengerGeneration!.Batches[0].CheckpointId;
        var flight = scenario.Inputs.Flights[0].Id;
        var corridor = scenario.Inputs.Navigation!.Graph.Nodes.Single(node => node.Name == "Coridor principal").Id;
        var start = scenario.Inputs.StartsAtUtc;
        ScenarioInputEvent Event(string type, int minute, Guid? checkpoint = null, Guid? flightId = null, string payload = "{}") =>
            new(Guid.NewGuid(), type, start.AddMinutes(minute), checkpoint, null, flightId, payload);
        var events = new[]
        {
            Event("checkpoint_unavailable", 1, control),
            Event("staff_reduction", 1, control, payload: "{\"staffCount\":0}"),
            Event("passenger_surge", 2, control, flight, "{\"passengerCount\":2,\"windowMinutes\":1}"),
            Event("flight_change", 2, flightId: flight, payload: "{\"delayMinutes\":1}"),
            Event("corridor_closure", 3, payload: JsonSerializer.Serialize(new { zoneId = corridor })),
            Event("resume", 5, control),
            Event("resume", 8, payload: JsonSerializer.Serialize(new { zoneId = corridor })),
        };
        foreach (var item in events)
        {
            using var saved = await host.Client.PostAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/events",
                new EventWriteRequest(scenario.Scenario.Revision, item), Json, Token);
            Assert.True(saved.StatusCode == HttpStatusCode.Created, await saved.Content.ReadAsStringAsync(Token));
            scenario = (await saved.Content.ReadFromJsonAsync<ScenarioDetails>(Json, Token))!;
        }
        // An invalid edit fails atomically; deleting the closure would orphan its resume.
        using var invalid = await host.Client.DeleteAsync($"/scenarios/{scenario.Scenario.Id}/events/{events[4].Id}?revision={scenario.Scenario.Revision}", Token);
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Contains("contradicts the scheduled timeline", await invalid.Content.ReadAsStringAsync(Token));
        var changed = await Run(host, scenario.Scenario.Id);
        var report = (await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{changed.Id}/processes", Json, Token))!;
        Assert.Equal(4, report.PopulationCount);
        Assert.Equal(4, report.Passengers.Select(person => person.Id).Distinct().Count());
        Assert.Equal(1, report.Passengers[0].SecurityClearedMinute);
        Assert.Equal(5, report.Passengers[1].ProcessingStartedMinute);
        Assert.Equal(new[] { "checkpoint_unavailable", "staff_reduction", "flight_change", "passenger_surge", "corridor_closure", "resume", "resume" },
            report.Events!.Select(item => item.Type));
        Assert.All(report.Passengers, person => Assert.Equal(4, person.BoardingCloseMinute));
        var kpis = (await host.Client.GetFromJsonAsync<KpiResult>($"/runs/{changed.Id}/kpis?limit=50000", Json, Token))!;
        Assert.All(kpis.Snapshots.Where(row => row.ZoneId == control && row.Minute >= 1 && row.Minute < 5),
            row => Assert.Equal(0, row.EffectiveCapacity));
        Assert.All(kpis.Snapshots.Where(row => row.Minute < 1), row =>
        {
            var before = baselineKpis.Snapshots.Single(item => item.ZoneId == row.ZoneId && item.Minute == row.Minute);
            Assert.Equal(before with { RunId = row.RunId, EffectiveCapacity = row.EffectiveCapacity }, row);
        });
        var preserved = (await host.Client.GetFromJsonAsync<ProcessResult>($"/runs/{baseline.Id}/processes", Json, Token))!;
        Assert.Equal(JsonSerializer.Serialize(baselineReport, Json), JsonSerializer.Serialize(preserved, Json));
    }

    [Fact]
    public async Task Kpis_persist_manual_formulas_temporal_snapshots_and_the_original_cadence_after_scenario_edits()
    {
        await using var host=await ManagementHost.CreateAsync(realEngine:true);
        var scenario=await Save(host,await Create(host),Config());
        var id=scenario.Scenario.Id;
        using var configured=await host.Client.PutAsJsonAsync($"/scenarios/{id}/kpis/settings",new KpiWriteRequest(scenario.Scenario.Revision,3),Json,Token);
        Assert.Equal(HttpStatusCode.OK,configured.StatusCode); scenario=(await configured.Content.ReadFromJsonAsync<ScenarioDetails>(Json,Token))!;
        var baseline=await Run(host,id);
        var zone=scenario.Inputs.PassengerGeneration!.Batches[0].CheckpointId;
        var report=(await host.Client.GetFromJsonAsync<KpiResult>($"/runs/{baseline.Id}/kpis?limit=50000",Json,Token))!;
        Assert.Equal(3,report.IntervalMinutes); Assert.Equal(2,report.ProcessedCount); Assert.Equal(0,report.UnprocessedCount);
        var rows=report.Snapshots.Where(row=>row.ZoneId==zone).ToArray();
        Assert.Equal(new[] {0,3,6,9,10},rows.Select(row=>row.Minute));
        Assert.Null(rows[0].MeanWaitMinutes); Assert.Equal(0,rows[0].WaitSampleCount);
        Assert.Equal(.5,rows[^1].MeanWaitMinutes); Assert.Equal(.5,rows[^1].MedianWaitMinutes); Assert.Equal(1,rows[^1].P95WaitMinutes);
        Assert.Equal(1,rows[^1].MaximumQueue); Assert.Equal(.2,rows[^1].CumulativeUtilization); Assert.Equal(.5,rows[^1].BoardingLateRisk);
        var states=(await host.Client.GetFromJsonAsync<RunResults<RunSnapshot>>($"/runs/{baseline.Id}/snapshots?zoneType=checkpoint&zoneId={zone}&limit=100",Json,Token))!;
        Assert.Equal(5,states.Items.Count);
        Assert.All(states.Items,item=>
        {
            var row=ContractJson.Read<KpiSnapshot>(JsonSerializer.Deserialize<JsonElement>(item.PayloadJson),"KpiSnapshot");
            Assert.Equal(baseline.Id,row.RunId); Assert.Equal(zone,row.ZoneId);
            Assert.Equal(scenario.Inputs.StartsAtUtc.AddMinutes(row.Minute),item.RecordedAtUtc);
        });
        var metrics=(await host.Client.GetFromJsonAsync<RunResults<RunMetric>>($"/runs/{baseline.Id}/metrics?zoneType=checkpoint&zoneId={zone}&limit=100",Json,Token))!;
        Assert.Equal(.5m,metrics.Items.Single(item=>item.Name=="medianWaitMinutes").Value);
        Assert.Equal(1m,metrics.Items.Single(item=>item.Name=="p95WaitMinutes").Value);
        var filtered=(await host.Client.GetFromJsonAsync<KpiResult>($"/runs/{baseline.Id}/kpis?zoneId={zone}&fromMinute=0&toMinute=3&offset=1&limit=1",Json,Token))!;
        Assert.Single(filtered.Snapshots); Assert.Equal(3,filtered.Snapshots[0].Minute); Assert.Equal(2,filtered.MatchedSnapshotCount); Assert.Equal(2,filtered.PopulationCount);
        using var invalid=await host.Client.PutAsJsonAsync($"/scenarios/{id}/kpis/settings",new KpiWriteRequest(scenario.Scenario.Revision,6),Json,Token);
        Assert.Equal(HttpStatusCode.BadRequest,invalid.StatusCode);
        using var stale=await host.Client.PutAsJsonAsync($"/scenarios/{id}/kpis/settings",new KpiWriteRequest(1,5),Json,Token);
        Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        using var changed=await host.Client.PutAsJsonAsync($"/scenarios/{id}/kpis/settings",new KpiWriteRequest(scenario.Scenario.Revision,5),Json,Token);
        Assert.Equal(HttpStatusCode.OK,changed.StatusCode);
        var next=await Run(host,id);
        var updated=(await host.Client.GetFromJsonAsync<KpiResult>($"/runs/{next.Id}/kpis?limit=50000",Json,Token))!;
        Assert.Equal(5,updated.IntervalMinutes);
        var preserved=(await host.Client.GetFromJsonAsync<KpiResult>($"/runs/{baseline.Id}/kpis?limit=50000",Json,Token))!;
        Assert.Equal(JsonSerializer.Serialize(report,Json),JsonSerializer.Serialize(preserved,Json));
    }
    [Fact]
    public async Task Navigation_requires_passenger_profiles()
    {
        await using var host=await ManagementHost.CreateAsync();
        var inactive=await Create(host,false);
        using var noProfiles=await host.Client.PutAsJsonAsync($"/scenarios/{inactive.Scenario.Id}/navigation/settings",new NavigationWriteRequest(inactive.Scenario.Revision,Config()),Json,Token);
        Assert.Equal(HttpStatusCode.BadRequest,noProfiles.StatusCode);
    }
    [Fact]
    public async Task Invalid_graph_edits_leave_the_saved_snapshot_and_revision_unchanged()
    {
        await using var host=await ManagementHost.CreateAsync(); var scenario=await Create(host); scenario=await Save(host,scenario,Config());
        var config=scenario.Inputs.Navigation!;
        var invalid=config with { Graph=config.Graph with { Edges=[config.Graph.Edges[0] with { TravelSeconds=0 },..config.Graph.Edges.Skip(1)] } };
        using var response=await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/navigation/settings",new NavigationWriteRequest(scenario.Scenario.Revision,invalid),Json,Token);
        Assert.Equal(HttpStatusCode.BadRequest,response.StatusCode);
        var unchanged=(await host.Client.GetFromJsonAsync<ScenarioDetails>($"/scenarios/{scenario.Scenario.Id}",Json,Token))!;
        Assert.Equal(scenario.Scenario.Revision,unchanged.Scenario.Revision);
        Assert.Equal(JsonSerializer.Serialize(config,Json),JsonSerializer.Serialize(unchanged.Inputs.Navigation,Json));
        using var stale=await host.Client.GetAsync($"/scenarios/{scenario.Scenario.Id}/navigation?revision=1",Token); Assert.Equal(HttpStatusCode.Conflict,stale.StatusCode);
        using var disabled=await host.Client.PutAsJsonAsync($"/scenarios/{scenario.Scenario.Id}/passengers/settings",new PassengerConfigWriteRequest(scenario.Scenario.Revision,42,null),Json,Token);
        Assert.Equal(HttpStatusCode.OK,disabled.StatusCode); Assert.Null((await disabled.Content.ReadFromJsonAsync<ScenarioDetails>(Json,Token))!.Inputs.Navigation);
    }
    [Fact]
    public async Task Saved_graph_snapshot_is_available_through_the_API_and_keeps_resource_bindings_explicit()
    {
        await using var host=await ManagementHost.CreateAsync();
        var airport=Guid.NewGuid(); var terminal=Guid.NewGuid(); var floor=Guid.NewGuid(); var entry=Guid.NewGuid(); var security=Guid.NewGuid(); var gate=Guid.NewGuid();
        var map=new AirportMap
        {
            Airport=new() { Id=airport,Name="Synthetic airport" }, Terminals=[new() { Id=terminal,AirportId=airport,Name="Terminal" }],
            Floors=[new() { Id=floor,AirportId=airport,TerminalId=terminal,Name="Floor" }],
            Zones=new[] { (entry,ZoneType.Entrance,"Entry",0d),(security,ZoneType.Security,"Security",5d),(gate,ZoneType.Gate,"Gate",10d) }
                .Select(item => new Zone { Id=item.Item1,Type=item.Item2,Name=item.Item3,X=item.Item4,Capacity=100,AirportId=airport,TerminalId=terminal,FloorId=floor }).ToList(),
            Routes=new[] { (entry,security),(security,gate) }.Select(pair => new Route { Id=Guid.NewGuid(),Name="Corridor",AirportId=airport,TerminalId=terminal,FromZoneId=pair.Item1,ToZoneId=pair.Item2,TravelDurationSeconds=30 }).ToList(),
            Checkpoints=[new() { Id=Guid.NewGuid(),Name="Control",AirportId=airport,TerminalId=terminal,ZoneId=security,Capacity=10,ProcessingRatePerMinute=7 }],
            Gates=[new() { Id=Guid.NewGuid(),Name="Gate",AirportId=airport,TerminalId=terminal,ZoneId=gate,Capacity=100,BoardingRatePerMinute=10 }]
        };
        var mapJson = new JsonSerializerOptions(Json);
        mapJson.Converters.Add(new JsonStringEnumConverter());
        using var created=await host.Client.PostAsJsonAsync("/api/airports",map,mapJson,Token);
        Assert.True(created.StatusCode == HttpStatusCode.Created, await created.Content.ReadAsStringAsync(Token));
        using var response=await host.Client.GetAsync($"/navigation/airports/{airport}",Token); Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        var graph=(await response.Content.ReadFromJsonAsync<NavigationConfig>(Json,Token))!;
        Assert.Equal(3,graph.Graph.Nodes.Count); Assert.Equal(2,graph.Graph.Edges.Count);
        Assert.Equal(map.Checkpoints[0].Id,graph.CheckpointBindings[0].ResourceId); Assert.Equal(security,graph.CheckpointBindings[0].ZoneId);
        Assert.Equal(5,graph.Graph.Edges[0].DistanceMeters); Assert.Equal(30,graph.Graph.Edges[0].TravelSeconds);
    }
}
