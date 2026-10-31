using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Simulation;
using AeroSynapse.Application.Storage;
using AeroSynapse.Application.Management;
using AeroSynapse.Domain.DigitalTwin;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Simulation;

public sealed class SimulationService(TwinDbContext database) : ISimulationService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<SimulationExecution> StartAsync(Guid scenarioId, Guid commandId, int revision, string correlationId, CancellationToken token)
    {
        if (commandId == Guid.Empty || revision <= 0)
            throw new ScenarioValidationException(new Dictionary<string, string[]> { ["command"] = ["A non-empty UUID Idempotency-Key and positive scenarioRevision are required."] });
        await using var transaction = await database.Database.BeginTransactionAsync(token);
        // Serialize equal keys even across scenarios and API instances. No HTTP call holds this lock.
        await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({commandId.ToString()}, 11003))", token);
        var existing = await database.SimulationCommands.AsNoTracking().SingleOrDefaultAsync(x => x.Id == commandId, token);
        if (existing is not null)
        {
            if (existing.ScenarioId != scenarioId || existing.ScenarioRevision != revision)
                throw new ScenarioConflictException("Idempotency-Key already identifies a different command.");
            return Describe(existing, await database.Runs.AsNoTracking().SingleAsync(x => x.Id == existing.RunId, token));
        }
        var run = await new ScenarioService(database).CreateRunCoreAsync(scenarioId, "pending", revision, token);
        var input = JsonSerializer.Deserialize<RunInputSnapshot>(run.InputSnapshotJson!, Json)!;
        var request = CapturedSimulation.Request(run.Id, input);
        try { ContractJson.Read<SimulationRequest>(JsonSerializer.SerializeToElement(request, ContractJson.Options), "SimulationRequest"); }
        catch (ContractValidationException error)
        {
            throw new ScenarioValidationException(error.Errors.GroupBy(x => x.Field).ToDictionary(x => x.Key, x => x.Select(e => e.Message).ToArray()));
        }
        var command = new SimulationCommand
        {
            Id = commandId, RunId = run.Id, ScenarioId = scenarioId, ScenarioRevision = revision,
            CorrelationId = correlationId, RequestJson = JsonSerializer.Serialize(request, ContractJson.Options), NextAttemptAtUtc = DateTime.UtcNow
        };
        database.SimulationCommands.Add(command);
        await database.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Describe(command, run);
    }

    public async Task<SimulationExecution?> GetAsync(Guid commandId, CancellationToken token) =>
        await (from command in database.SimulationCommands.AsNoTracking()
               join run in database.Runs.AsNoTracking() on command.RunId equals run.Id
               where command.Id == commandId
               select new SimulationExecution(command.Id, run.Id, command.ScenarioId, run.Status,
                   command.CorrelationId, run.Status == "completed", command.ErrorCode, run.FailureReason)).SingleOrDefaultAsync(token);

    private static SimulationExecution Describe(SimulationCommand command, SimulationRun run) =>
        new(command.Id, run.Id, command.ScenarioId, run.Status, command.CorrelationId, run.ResultsValid, command.ErrorCode, run.FailureReason);
}
