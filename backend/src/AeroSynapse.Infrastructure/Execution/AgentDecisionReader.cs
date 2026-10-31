using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;

namespace AeroSynapse.Infrastructure.Execution;

/// <summary>The agent's decisions in a run, in order, while it runs and after.</summary>
public sealed class AgentDecisionReader(TwinDbContext db)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AgentDecisionView>> ListAsync(Guid runId, CancellationToken token)
    {
        if (!await db.Runs.AsNoTracking().AnyAsync(run => run.Id == runId, token)) throw new ScenarioNotFoundException("Run was not found.");
        var rows = await db.AgentDecisions.AsNoTracking().Where(d => d.RunId == runId).OrderBy(d => d.Sequence).ToListAsync(token);
        return rows.Select(d =>
        {
            var options = JsonSerializer.Deserialize<AgentOption[]>(d.OptionsJson, Json)!;
            var picked = JsonSerializer.Deserialize<AgentOption>(d.ActionJson, Json)!;
            return new AgentDecisionView(d.Sequence, d.Minute, d.Point, d.Kind, d.OptionId, picked.Text, d.Reason, d.ModelStatus, d.Model, options, d.RecordedAtUtc);
        }).ToArray();
    }
}
