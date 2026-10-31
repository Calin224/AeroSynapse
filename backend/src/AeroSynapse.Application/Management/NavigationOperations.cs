using System.Text.Json;
using AeroSynapse.Application.Airports;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using MediatR;

namespace AeroSynapse.Application.Management;

public sealed record NavigationResource(Guid Id,string Name);
public sealed record NavigationSettings(Guid ScenarioId,int Revision,bool PassengerProfilesActive,NavigationConfig? Config,
    NavigationConfig Defaults,IReadOnlyList<NavigationResource> Checkpoints,IReadOnlyList<NavigationResource> Gates);
public sealed record NavigationWriteRequest(int Revision,NavigationConfig? Config);
public sealed record GetNavigationSettingsQuery(Guid ScenarioId) : IRequest<NavigationSettings>;
public sealed record WriteNavigationCommand(Guid ScenarioId,NavigationWriteRequest Value) : IRequest<ScenarioDetails>;
public sealed record PreviewNavigationQuery(Guid ScenarioId,int Revision,int Offset=0,int Limit=100, NavigationConfig? Config=null) : IRequest<NavigationResult>;
public sealed record GetRunNavigationQuery(Guid RunId,int Offset=0,int Limit=100) : IRequest<NavigationResult>;
public sealed record ImportNavigationMapQuery(Guid AirportId) : IRequest<NavigationConfig>;

public sealed class NavigationOperations(IScenarioService scenarios,IRunStorageService runs,INavigationPlanner planner,IAirportMapRepository maps) :
    IRequestHandler<GetNavigationSettingsQuery,NavigationSettings>,IRequestHandler<WriteNavigationCommand,ScenarioDetails>,
    IRequestHandler<PreviewNavigationQuery,NavigationResult>,IRequestHandler<GetRunNavigationQuery,NavigationResult>,IRequestHandler<ImportNavigationMapQuery,NavigationConfig>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<NavigationSettings> Handle(GetNavigationSettingsQuery query,CancellationToken token)
    {
        var scenario = await Require(query.ScenarioId,token); var layout = await Layout(scenario,token);
        return new(query.ScenarioId,scenario.Scenario.Revision,scenario.Inputs.PassengerGeneration is not null,scenario.Inputs.Navigation,
            NavigationScenarios.Defaults(layout.Layout),layout.Layout.Checkpoints.Select(item => new NavigationResource(item.Id,item.Name)).ToArray(),
            layout.Layout.Gates.Select(item => new NavigationResource(item.Id,item.Name)).ToArray());
    }
    public async Task<ScenarioDetails> Handle(WriteNavigationCommand command,CancellationToken token)
    {
        var scenario = await Require(command.ScenarioId,token); Revision(scenario,command.Value.Revision);
        var layout = await Layout(scenario,token);
        if (scenario.Scenario.DeletedAtUtc is not null) throw new ScenarioConflictException("Archived scenarios cannot be edited.");
        var inputs = scenario.Inputs with { Navigation=command.Value.Config }; NavigationScenarios.Validate(inputs,layout.Layout);
        return await scenarios.UpdateAsync(command.ScenarioId,new(scenario.Scenario.Code,scenario.Scenario.Name,scenario.Scenario.Description,
            scenario.Scenario.SceneId,scenario.Scenario.LayoutVersionId,inputs,command.Value.Revision),token);
    }
    public async Task<NavigationResult> Handle(PreviewNavigationQuery query,CancellationToken token)
    {
        Pagination(query.Offset,query.Limit);
        var scenario = await Require(query.ScenarioId,token); Revision(scenario,query.Revision); var layout = await Layout(scenario,token);
        var inputs = query.Config is { } draft ? scenario.Inputs with { Navigation = draft } : scenario.Inputs;
        NavigationScenarios.Validate(inputs, layout.Layout);
        return await planner.PreviewAsync(new(PassengerScenarios.Request(inputs,layout.Layout),NavigationScenarios.Input(inputs,layout.Layout)),query.Offset,query.Limit,token);
    }
    public async Task<NavigationResult> Handle(GetRunNavigationQuery query,CancellationToken token)
    {
        Pagination(query.Offset,query.Limit);
        var run = await runs.GetAsync(query.RunId,token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (run.Status != "completed") throw new ScenarioConflictException("Boarding assessment is available after the run completes.");
        var snapshots = await runs.SnapshotsAsync(query.RunId,new(ZoneType:"scene",ZoneId:run.SceneId,Limit:100),token);
        foreach (var snapshot in snapshots.Items)
        {
            using var document = JsonDocument.Parse(snapshot.PayloadJson);
            if (document.RootElement.TryGetProperty("kind",out var kind) && kind.GetString() is "navigation/v1" or "processes/v1"
                && document.RootElement.TryGetProperty("navigationResult", out _))
            {
                var result = ContractJson.Read<NavigationResult>(document.RootElement.GetProperty("navigationResult"),"NavigationResult");
                var items = result.Journeys.Skip(query.Offset).Take(query.Limit).ToArray();
                return result with { Journeys=items,Offset=query.Offset,ReturnedCount=items.Length,HasMore=query.Offset+items.Length<result.ModeledCount };
            }
        }
        throw new ScenarioNotFoundException("The run has no captured navigation result. Enable navigation before executing a new run.");
    }
    public async Task<NavigationConfig> Handle(ImportNavigationMapQuery query,CancellationToken token) =>
        NavigationGraphImporter.FromMap(await maps.GetAsync(query.AirportId,token) ?? throw new ScenarioNotFoundException("Airport map was not found."));
    private async Task<ScenarioDetails> Require(Guid id,CancellationToken token) => await scenarios.GetAsync(id,token) ?? throw new ScenarioNotFoundException("Scenario was not found.");
    private async Task<PublishedLayout> Layout(ScenarioDetails scenario,CancellationToken token) =>
        await scenarios.GetLayoutAsync(scenario.Scenario.SceneId,scenario.Scenario.LayoutVersionId,token) ?? throw new ScenarioNotFoundException("Layout was not found.");
    private static void Revision(ScenarioDetails scenario,int revision)
    {
        if (revision <= 0) throw new ScenarioValidationException(new Dictionary<string,string[]> { ["revision"]=["A positive revision is required."] });
        if (revision != scenario.Scenario.Revision) throw new ScenarioConflictException("Scenario has changed. Reload before routing passengers.");
    }
    public static void Pagination(int offset,int limit)
    {
        if (offset is <0 or >100000 || limit is <1 or >100000)
            throw new ScenarioValidationException(new Dictionary<string,string[]> { ["pagination"]=["Offset: 0–100000; limit: 1–100000."] });
    }
}
