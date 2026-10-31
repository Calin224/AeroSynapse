using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Infrastructure.Execution;

public sealed partial class BatchService
{
    // One transaction owns the scenario copy, the pair of immutable captures and its durable manifest.
    // A replay of the command returns the same pair, even after the source has been edited.
    public async Task<BatchDetails> CreateWhatIfAsync(Guid scenarioId, CreateWhatIfRequest request, CancellationToken token)
    {
        Validate(request.CommandId, request.Name, request.TimeLimitSeconds);
        if (request.Revision <= 0) throw Invalid("revision", "The current positive scenario revision is required.");
        if (request.StaffCount < 0) throw Invalid("staffCount", "Staff count must be zero or a positive integer.");
        var identity = JsonSerializer.Serialize(new { kind = "what-if/v1", scenarioId, request }, Json);
        await using (var transaction = await db.Database.BeginTransactionAsync(token))
        {
            if (!await Exists(request.CommandId, identity, token))
            {
                await LockScenarios([scenarioId], token);
                var source = await scenarios.GetAsync(scenarioId, token) ?? throw new ScenarioNotFoundException("Scenario was not found.");
                if (source.Scenario.DeletedAtUtc is not null) throw new ScenarioConflictException("Archived scenarios cannot create what-if runs.");
                if (source.Scenario.Revision != request.Revision) throw new ScenarioConflictException("Scenario has changed. Reload before creating the pair.");
                source = source with { Inputs = FlightScheduling.Resolve(source.Inputs) };
                var layout = await scenarios.GetLayoutAsync(source.Scenario.SceneId, source.Scenario.LayoutVersionId, token)
                    ?? throw new ScenarioNotFoundException("Captured layout was not found.");
                ScenarioValidator.Validate(new(source.Scenario.Code, source.Scenario.Name, source.Scenario.Description,
                    source.Scenario.SceneId, source.Scenario.LayoutVersionId, source.Inputs), layout.Layout);
                // The post is a security checkpoint or the scenario's own check-in desks.
                ScenarioInputs inputs;
                if (source.Inputs.CheckIn is { } checkIn && request.CheckpointId == checkIn.AreaId)
                {
                    var desks = layout.Layout.CheckInAreas?.SingleOrDefault(area => area.Id == checkIn.AreaId)?.DeskCount ?? 0;
                    if (request.StaffCount < 1 || request.StaffCount > desks) throw Invalid("staffCount", $"Open 1 to {desks} check-in desks.");
                    inputs = source.Inputs with { CheckIn = checkIn with { StaffCount = request.StaffCount },
                        Operators = OperatorScenarios.WithDesks(source.Inputs.Operators, request.StaffCount) };
                }
                else
                {
                    var checkpoint = layout.Layout.Checkpoints.FirstOrDefault(c => c.Id == request.CheckpointId)
                        ?? throw Invalid("checkpointId", "Choose a checkpoint or the check-in desks of the scenario.");
                    var current = source.Inputs.StaffAllocations.Where(a => a.CheckpointId == checkpoint.Id).OrderBy(a => a.StaffMemberId).ToArray();
                    var allocated = source.Inputs.StaffAllocations.Select(a => a.StaffMemberId).ToHashSet();
                    // A scenario with its own team adds only its unassigned security operators, not the layout's spare slots.
                    var team = source.Inputs.Operators?.Where(person => person.Role == "security").Select(person => person.Id).ToHashSet();
                    var available = layout.Layout.Staff.Where(member => member.TerminalId == checkpoint.TerminalId && !allocated.Contains(member.Id)
                            && (team is null || team.Contains(member.Id)))
                        .OrderBy(member => member.Id).ToArray();
                    if (request.StaffCount > current.Length + available.Length)
                        throw Invalid("staffCount", $"Only {current.Length + available.Length} staff members are available at this checkpoint without taking people allocated elsewhere.");
                    inputs = source.Inputs with { StaffAllocations = source.Inputs.StaffAllocations.Where(a => a.CheckpointId != checkpoint.Id)
                        .Concat(current.Take(request.StaffCount))
                        .Concat(available.Take(Math.Max(0, request.StaffCount - current.Length)).Select(member => new StaffAllocation(member.Id, checkpoint.Id))).ToArray() };
                }
                var copyName = request.Name.Trim(); if (copyName.Length > 145) copyName = copyName[..(char.IsHighSurrogate(copyName[144]) ? 144 : 145)];
                var candidate = await scenarios.CreateAsync(new($"WHATIF-{request.CommandId:N}", copyName + " · what-if", source.Scenario.Description,
                    source.Scenario.SceneId, source.Scenario.LayoutVersionId, inputs), token);
                candidate = await CarryDataSource(source, candidate, token);
                var now = DateTime.UtcNow;
                RunInputSnapshot Capture(ScenarioDetails value) => new(1, now, value, layout, KpiConventions.Engine, value.Inputs.SnapshotIntervalMinutes ?? 1);
                Add(request.CommandId, request.Name, request.TimeLimitSeconds, [source.Inputs.Seed], [Capture(source), Capture(candidate)], identity, null, now);
                await db.SaveChangesAsync(token);
            }
            await transaction.CommitAsync(token);
        }
        return await GetAsync(request.CommandId, token);
    }
}
