using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using MediatR;
namespace AeroSynapse.Application.Management;

public sealed record FlightWriteRequest(int Revision, ScenarioFlight Flight);
public sealed record FlightCsvImportRequest(int Revision, string Csv);
public sealed record GetFlightScheduleQuery(Guid ScenarioId) : IRequest<FlightSchedule>;
public sealed record ExportFlightScheduleQuery(Guid ScenarioId) : IRequest<string>;
public sealed record ImportFlightScheduleCommand(Guid ScenarioId, FlightCsvImportRequest Value) : IRequest<ScenarioDetails>;
public sealed record EventWriteRequest(int Revision, ScenarioInputEvent Event);
public sealed record ScenarioItems<T>(Guid ScenarioId, int Revision, IReadOnlyList<T> Items);
public sealed record ListScenariosQuery(Guid? SceneId) : IRequest<IReadOnlyList<ScenarioSummary>>;
public sealed record GetScenarioQuery(Guid Id) : IRequest<ScenarioDetails>;
public sealed record CreateScenarioCommand(ScenarioWriteRequest Value) : IRequest<ScenarioDetails>;
public sealed record ValidateScenarioQuery(ScenarioWriteRequest Value) : IRequest<ScenarioValidationResult>;
public sealed record UpdateScenarioCommand(Guid Id, ScenarioWriteRequest Value) : IRequest<ScenarioDetails>;
public sealed record DuplicateScenarioCommand(Guid Id, DuplicateScenarioRequest Value) : IRequest<ScenarioDetails>;
public sealed record PublishLayoutCommand(Guid SceneId) : IRequest<PublishedLayout>;
public sealed record ListLayoutsQuery(Guid SceneId) : IRequest<IReadOnlyList<LayoutVersionSummary>>;
public sealed record GetLayoutQuery(Guid SceneId, Guid VersionId) : IRequest<PublishedLayout>;
public sealed record GetFlightsQuery(Guid ScenarioId) : IRequest<ScenarioItems<ScenarioFlight>>;
public sealed record GetInputEventsQuery(Guid ScenarioId) : IRequest<ScenarioItems<ScenarioInputEvent>>;
public sealed record WriteFlightCommand(Guid ScenarioId, Guid? FlightId, FlightWriteRequest Value) : IRequest<ScenarioDetails>;
public sealed record DeleteFlightCommand(Guid ScenarioId, Guid FlightId, int Revision) : IRequest<ScenarioDetails>;
public sealed record WriteInputEventCommand(Guid ScenarioId, Guid? EventId, EventWriteRequest Value) : IRequest<ScenarioDetails>;
public sealed record DeleteInputEventCommand(Guid ScenarioId, Guid EventId, int Revision) : IRequest<ScenarioDetails>;

public sealed class ScenarioOperations(IScenarioService scenarios, ISceneQueryService scenes) :
    IRequestHandler<ListScenariosQuery, IReadOnlyList<ScenarioSummary>>, IRequestHandler<GetScenarioQuery, ScenarioDetails>,
    IRequestHandler<CreateScenarioCommand, ScenarioDetails>, IRequestHandler<UpdateScenarioCommand, ScenarioDetails>, IRequestHandler<ValidateScenarioQuery, ScenarioValidationResult>,
    IRequestHandler<DuplicateScenarioCommand, ScenarioDetails>, IRequestHandler<PublishLayoutCommand, PublishedLayout>,
    IRequestHandler<ListLayoutsQuery, IReadOnlyList<LayoutVersionSummary>>, IRequestHandler<GetLayoutQuery, PublishedLayout>,
    IRequestHandler<GetFlightsQuery, ScenarioItems<ScenarioFlight>>, IRequestHandler<GetInputEventsQuery, ScenarioItems<ScenarioInputEvent>>,
    IRequestHandler<WriteFlightCommand, ScenarioDetails>, IRequestHandler<DeleteFlightCommand, ScenarioDetails>,
    IRequestHandler<WriteInputEventCommand, ScenarioDetails>, IRequestHandler<DeleteInputEventCommand, ScenarioDetails>,
    IRequestHandler<GetFlightScheduleQuery, FlightSchedule>, IRequestHandler<ExportFlightScheduleQuery, string>,
    IRequestHandler<ImportFlightScheduleCommand, ScenarioDetails>
{
    public async Task<IReadOnlyList<ScenarioSummary>> Handle(ListScenariosQuery request, CancellationToken token)
    {
        if (request.SceneId is { } id) await RequireScene(id, token);
        return await scenarios.ListAsync(request.SceneId, token);
    }
    public async Task<ScenarioDetails> Handle(GetScenarioQuery request, CancellationToken token) =>
        await scenarios.GetAsync(request.Id, token) ?? throw new ScenarioNotFoundException("Scenario was not found.");
    public Task<ScenarioDetails> Handle(CreateScenarioCommand request, CancellationToken token) => scenarios.CreateAsync(request.Value, token);
    public Task<ScenarioValidationResult> Handle(ValidateScenarioQuery request, CancellationToken token) => scenarios.ValidateAsync(request.Value, token);
    public Task<ScenarioDetails> Handle(UpdateScenarioCommand request, CancellationToken token) => scenarios.UpdateAsync(request.Id, request.Value, token);
    public Task<ScenarioDetails> Handle(DuplicateScenarioCommand request, CancellationToken token) => scenarios.DuplicateAsync(request.Id, request.Value, token);
    public Task<PublishedLayout> Handle(PublishLayoutCommand request, CancellationToken token) => scenarios.PublishLayoutAsync(request.SceneId, token);
    public async Task<IReadOnlyList<LayoutVersionSummary>> Handle(ListLayoutsQuery request, CancellationToken token)
    {
        await RequireScene(request.SceneId, token); return await scenarios.LayoutVersionsAsync(request.SceneId, token);
    }
    public async Task<PublishedLayout> Handle(GetLayoutQuery request, CancellationToken token) =>
        await scenarios.GetLayoutAsync(request.SceneId, request.VersionId, token) ?? throw new ScenarioNotFoundException("Layout version was not found.");
    public async Task<ScenarioItems<ScenarioFlight>> Handle(GetFlightsQuery request, CancellationToken token)
    {
        var value = await Handle(new GetScenarioQuery(request.ScenarioId), token);
        return new(request.ScenarioId, value.Scenario.Revision, value.Inputs.Flights);
    }
    public async Task<FlightSchedule> Handle(GetFlightScheduleQuery request, CancellationToken token)
    {
        var value = await Handle(new GetScenarioQuery(request.ScenarioId), token);
        var layout = await Handle(new GetLayoutQuery(value.Scenario.SceneId, value.Scenario.LayoutVersionId), token);
        return new(request.ScenarioId, value.Scenario.Revision, value.Inputs.StartsAtUtc, value.Inputs.DurationMinutes,
            FlightScheduling.Resolve(value.Inputs).Flights,
            layout.Layout.Gates.Select(gate => new ScheduleGate(gate.Id, gate.Code, gate.Name, gate.Capacity)).ToArray());
    }
    public async Task<string> Handle(ExportFlightScheduleQuery request, CancellationToken token) =>
        FlightScheduleCsv.Export(await Handle(new GetFlightScheduleQuery(request.ScenarioId), token));
    public async Task<ScenarioDetails> Handle(ImportFlightScheduleCommand request, CancellationToken token)
    {
        var value = await Editable(request.ScenarioId, request.Value.Revision, token);
        var schedule = await Handle(new GetFlightScheduleQuery(request.ScenarioId), token);
        var flights = FlightScheduleCsv.Import(request.Value.Csv, schedule.Gates);
        return await SaveFlights(value, flights, token);
    }
    public async Task<ScenarioItems<ScenarioInputEvent>> Handle(GetInputEventsQuery request, CancellationToken token)
    {
        var value = await Handle(new GetScenarioQuery(request.ScenarioId), token);
        return new(request.ScenarioId, value.Scenario.Revision, value.Inputs.Events);
    }
    public async Task<ScenarioDetails> Handle(WriteFlightCommand request, CancellationToken token)
    {
        var value = await Editable(request.ScenarioId, request.Value.Revision, token);
        var flight = request.Value.Flight ?? throw Invalid("flight", "A flight is required.");
        return await SaveFlights(value, Change(value.Inputs.Flights, flight, request.FlightId, x => x.Id), token);
    }
    public async Task<ScenarioDetails> Handle(DeleteFlightCommand request, CancellationToken token)
    {
        var value = await Editable(request.ScenarioId, request.Revision, token);
        RequireItem(value.Inputs.Flights, request.FlightId, x => x.Id);
        return await SaveFlights(value, value.Inputs.Flights.Where(x => x.Id != request.FlightId).ToArray(), token);
    }
    public async Task<ScenarioDetails> Handle(WriteInputEventCommand request, CancellationToken token)
    {
        var value = await Editable(request.ScenarioId, request.Value.Revision, token);
        var item = request.Value.Event ?? throw Invalid("event", "An event is required.");
        return await Save(value, value.Inputs with { Events = Change(value.Inputs.Events, item, request.EventId, x => x.Id) }, token);
    }
    public async Task<ScenarioDetails> Handle(DeleteInputEventCommand request, CancellationToken token)
    {
        var value = await Editable(request.ScenarioId, request.Revision, token);
        RequireItem(value.Inputs.Events, request.EventId, x => x.Id);
        return await Save(value, value.Inputs with { Events = value.Inputs.Events.Where(x => x.Id != request.EventId).ToArray() }, token);
    }
    private async Task<ScenarioDetails> Editable(Guid id, int revision, CancellationToken token)
    {
        if (revision <= 0) throw Invalid("revision", "The current positive scenario revision is required.");
        var value = await Handle(new GetScenarioQuery(id), token);
        if (value.Scenario.DeletedAtUtc is not null) throw new ScenarioConflictException("Archived scenarios cannot be edited.");
        if (value.Scenario.Revision != revision) throw new ScenarioConflictException("Scenario has changed. Reload it before editing.");
        return value;
    }
    private Task<ScenarioDetails> Save(ScenarioDetails value, ScenarioInputs inputs, CancellationToken token) =>
        scenarios.UpdateAsync(value.Scenario.Id, new(value.Scenario.Code, value.Scenario.Name, value.Scenario.Description,
            value.Scenario.SceneId, value.Scenario.LayoutVersionId, inputs, value.Scenario.Revision), token);
    private async Task<ScenarioDetails> SaveFlights(ScenarioDetails value, IReadOnlyList<ScenarioFlight> flights, CancellationToken token)
    {
        var changedGates = flights.Where(flight => value.Inputs.Flights.Any(old => old.Id == flight.Id && old.GateId != flight.GateId))
            .GroupBy(flight => flight.Id).ToDictionary(group => group.Key, group => group.First().GateId);
        var flows = value.Inputs.Flows;
        var events = value.Inputs.Events;
        if (changedGates.Count > 0)
        {
            var layout = await Handle(new GetLayoutQuery(value.Scenario.SceneId, value.Scenario.LayoutVersionId), token);
            flows = flows.Select(flow =>
            {
                if (!changedGates.TryGetValue(flow.FlightId, out var gate)) return flow;
                var oldRoute = layout.Layout.Routes.Single(route => route.Id == flow.RouteId);
                var route = layout.Layout.Routes.SingleOrDefault(route => route.FromCheckpointId == oldRoute.FromCheckpointId && route.ToGateId == gate)
                    ?? throw Invalid("flight.gateId", "No route connects the passenger flow's checkpoint to the selected gate.");
                return flow with { RouteId = route.Id };
            }).ToArray();
            events = events.Select(item => item.FlightId is { } id && item.GateId is not null && changedGates.TryGetValue(id, out var gate)
                ? item with { GateId = gate } : item).ToArray();
        }
        var inputs = value.Inputs with { Flights = flights, Flows = flows, Events = events };
        if (value.Inputs.PassengerGeneration is not null)
        {
            var layout = await Handle(new GetLayoutQuery(value.Scenario.SceneId, value.Scenario.LayoutVersionId), token);
            // Check the edited schedule before deriving timestamps and synchronizing passenger counts.
            ScenarioValidator.Validate(new(value.Scenario.Code, value.Scenario.Name, value.Scenario.Description,
                value.Scenario.SceneId, value.Scenario.LayoutVersionId,
                inputs with { PassengerGeneration = null, Navigation = null, Populations = [], Flows = [], Events = [] }), layout.Layout);
            inputs = PassengerScenarios.ReconcileFlights(value.Inputs, inputs, layout.Layout);
        }
        return await Save(value, inputs, token);
    }
    private static IReadOnlyList<T> Change<T>(IReadOnlyList<T> items, T item, Guid? routeId, Func<T, Guid> id)
    {
        if (routeId is { } existing)
        {
            RequireItem(items, existing, id);
            if (id(item) != existing) throw Invalid("id", "Body identifier must match the URL identifier.");
            return items.Select(x => id(x) == existing ? item : x).ToArray();
        }
        if (items.Any(x => id(x) == id(item))) throw new ScenarioConflictException("An item with this identifier already exists.");
        return [.. items, item];
    }
    private static void RequireItem<T>(IReadOnlyList<T> items, Guid id, Func<T, Guid> key)
    {
        if (!items.Any(x => key(x) == id)) throw new ScenarioNotFoundException("Scenario item was not found.");
    }
    private async Task RequireScene(Guid id, CancellationToken token)
    {
        if (await scenes.GetAsync(id, token) is null) throw new ScenarioNotFoundException("Scene was not found.");
    }
    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
