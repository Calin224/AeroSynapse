using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using MediatR;

namespace AeroSynapse.Application.Management;

public sealed record PassengerCheckpointChoice(Guid Id, string Name, double CapacityPerMinute);
public sealed record PassengerFlightChoice(Guid Id, string Code, int Capacity, bool IsCancelled, double BoardingCloseMinute, IReadOnlyList<Guid> CheckpointIds);
public sealed record PassengerSettings(Guid ScenarioId, int Revision, int Seed, PassengerGenerationConfig? Config,
    PassengerGenerationConfig Defaults, IReadOnlyList<PassengerCheckpointChoice> Checkpoints, IReadOnlyList<PassengerFlightChoice> Flights);
public sealed record PassengerConfigWriteRequest(int Revision, int Seed, PassengerGenerationConfig? Config);
public sealed record GetPassengerSettingsQuery(Guid ScenarioId) : IRequest<PassengerSettings>;
public sealed record WritePassengerConfigCommand(Guid ScenarioId, PassengerConfigWriteRequest Value) : IRequest<ScenarioDetails>;
public sealed record PreviewPassengersQuery(Guid ScenarioId, int Revision, int Offset = 0, int Limit = 100) : IRequest<PassengerGenerationResult>;
public sealed record ReplayRunPassengersQuery(Guid RunId, int Offset = 0, int Limit = 100) : IRequest<PassengerGenerationResult>;
public sealed record AnalyzePassengerFlowsQuery(Guid ScenarioId, int Revision) : IRequest<PassengerFlowReport>;
public sealed record ReplayRunPassengerFlowsQuery(Guid RunId) : IRequest<PassengerFlowReport>;

public sealed class PassengerOperations(IScenarioService scenarios, IPassengerGenerator generator, IRunStorageService runs) :
    IRequestHandler<GetPassengerSettingsQuery, PassengerSettings>, IRequestHandler<WritePassengerConfigCommand, ScenarioDetails>,
    IRequestHandler<PreviewPassengersQuery, PassengerGenerationResult>, IRequestHandler<ReplayRunPassengersQuery, PassengerGenerationResult>,
    IRequestHandler<AnalyzePassengerFlowsQuery, PassengerFlowReport>, IRequestHandler<ReplayRunPassengerFlowsQuery, PassengerFlowReport>
{
    public async Task<PassengerSettings> Handle(GetPassengerSettingsQuery query, CancellationToken token)
    {
        var scenario = await Require(query.ScenarioId, token);
        var layout = await Layout(scenario, token);
        // Derived profiles keep the aggregate counts; saving them explicitly also opens every checkpoint toward the gate.
        var defaults = scenario.Inputs.PassengerGeneration is null && IncidentScenarios.DerivesProfiles(scenario.Inputs)
            ? PassengerScenarios.Spread(IncidentScenarios.BaseConfig(scenario.Inputs, layout.Layout))
            : PassengerScenarios.SpreadDefaults(scenario.Inputs, layout.Layout);
        return new(query.ScenarioId, scenario.Scenario.Revision, scenario.Inputs.Seed, scenario.Inputs.PassengerGeneration, defaults,
            layout.Layout.Checkpoints.Select(checkpoint => new PassengerCheckpointChoice(checkpoint.Id, checkpoint.Name, checkpoint.CapacityPerMinute)).ToArray(),
            scenario.Inputs.Flights.Select(flight => new PassengerFlightChoice(flight.Id, flight.Code, flight.PassengerCapacity, flight.Status == "cancelled",
                (FlightScheduling.Resolve(flight, scenario.Inputs.StartsAtUtc).BoardingClosesAtUtc!.Value.AddMinutes(flight.DelayMinutes) - scenario.Inputs.StartsAtUtc).TotalMinutes,
                layout.Layout.Routes.Where(route => route.ToGateId == flight.GateId).Select(route => route.FromCheckpointId).Distinct().ToArray())).ToArray());
    }
    public async Task<ScenarioDetails> Handle(WritePassengerConfigCommand command, CancellationToken token)
    {
        var scenario = await Require(command.ScenarioId, token);
        Revision(scenario, command.Value.Revision);
        if (scenario.Scenario.DeletedAtUtc is not null) throw new ScenarioConflictException("Archived scenarios cannot be edited.");
        var layout = await Layout(scenario, token);
        var inputs = scenario.Inputs with { Seed = command.Value.Seed, PassengerGeneration = command.Value.Config };
        if (command.Value.Config is null) inputs = inputs with { Navigation = null };
        if (inputs.PassengerGeneration is not null) inputs = PassengerScenarios.Synchronize(inputs, layout.Layout);
        return await scenarios.UpdateAsync(command.ScenarioId, new(scenario.Scenario.Code, scenario.Scenario.Name, scenario.Scenario.Description,
            scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId, inputs, command.Value.Revision), token);
    }
    public async Task<PassengerGenerationResult> Handle(PreviewPassengersQuery query, CancellationToken token)
    {
        var scenario = await Require(query.ScenarioId, token);
        Revision(scenario, query.Revision);
        var layout = await Layout(scenario, token);
        return await Generate(scenario.Inputs, layout.Layout, query.Offset, query.Limit, token);
    }
    public async Task<PassengerGenerationResult> Handle(ReplayRunPassengersQuery query, CancellationToken token)
    {
        var captured = await Captured(query.RunId, token);
        return await Generate(captured.Scenario.Inputs, captured.Layout.Layout, query.Offset, query.Limit, token);
    }
    public async Task<PassengerFlowReport> Handle(AnalyzePassengerFlowsQuery query, CancellationToken token)
    {
        var scenario = await Require(query.ScenarioId, token);
        Revision(scenario, query.Revision);
        var layout = await Layout(scenario, token);
        return await generator.AnalyzeFlowsAsync(PassengerScenarios.Request(scenario.Inputs, layout.Layout), token);
    }
    public async Task<PassengerFlowReport> Handle(ReplayRunPassengerFlowsQuery query, CancellationToken token)
    {
        var captured = await Captured(query.RunId, token);
        return await generator.AnalyzeFlowsAsync(PassengerScenarios.Request(captured.Scenario.Inputs, captured.Layout.Layout), token);
    }
    private async Task<RunInputSnapshot> Captured(Guid runId, CancellationToken token)
    {
        var run = await runs.GetAsync(runId, token) ?? throw new ScenarioNotFoundException("Run was not found.");
        if (run.InputSnapshotJson is null) throw new ScenarioConflictException("The run has no captured passenger inputs.");
        return JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }
    private Task<PassengerGenerationResult> Generate(ScenarioInputs inputs, SceneDetails layout, int offset, int limit, CancellationToken token)
    {
        if (offset is < 0 or > 100000 || limit is < 1 or > 100000)
            throw new ScenarioValidationException(new Dictionary<string, string[]> { ["pagination"] = ["Offset: 0–100000; limit: 1–100000."] });
        return generator.GenerateAsync(PassengerScenarios.Request(inputs, layout), offset, limit, token);
    }
    private async Task<ScenarioDetails> Require(Guid id, CancellationToken token) => await scenarios.GetAsync(id, token)
        ?? throw new ScenarioNotFoundException("Scenario was not found.");
    private async Task<PublishedLayout> Layout(ScenarioDetails scenario, CancellationToken token) =>
        await scenarios.GetLayoutAsync(scenario.Scenario.SceneId, scenario.Scenario.LayoutVersionId, token)
        ?? throw new ScenarioNotFoundException("Layout was not found.");
    private static void Revision(ScenarioDetails scenario, int revision)
    {
        if (revision <= 0) throw new ScenarioValidationException(new Dictionary<string, string[]> { ["revision"] = ["The current positive revision is required."] });
        if (revision != scenario.Scenario.Revision) throw new ScenarioConflictException("Scenario has changed. Reload before generating passengers.");
    }
}
