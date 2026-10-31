using AeroSynapse.Application.Contracts;

namespace AeroSynapse.Application.Management;

/// <summary>One side of the comparison, measured like the Control Tower KPI tiles.</summary>
public sealed record AgentImpactSide(double? MeanWaitMinutes, double? MaxP95WaitMinutes, int MaxQueue, int? QueueClearedMinute);

/// <summary>What the agent changed in a run, and the same day simulated without those changes.</summary>
public sealed record AgentImpact(IReadOnlyList<string> Changes, AgentImpactSide WithoutAgent, AgentImpactSide WithAgent);

public static class AgentImpactSummary
{
    /// <summary>
    /// Waits weighted by completed waits at the last minute, the worst control's P95, the longest queue at any control,
    /// and the first minute after the busiest one when nobody waits anywhere (null when the queue never clears).
    /// </summary>
    public static AgentImpactSide From(IReadOnlyList<KpiSnapshot> snapshots)
    {
        if (snapshots.Count == 0) return new(null, null, 0, null);
        var horizon = snapshots.Max(s => s.Minute);
        var final = snapshots.Where(s => s.Minute == horizon && s.WaitSampleCount > 0).ToArray();
        var samples = final.Sum(s => s.WaitSampleCount);
        double? mean = samples > 0 && final.All(s => s.MeanWaitMinutes is not null)
            ? final.Sum(s => s.WaitSampleCount * s.MeanWaitMinutes!.Value) / samples : null;
        double? p95 = final.Length > 0 && final.All(s => s.P95WaitMinutes is not null) ? final.Max(s => s.P95WaitMinutes!.Value) : null;
        var queue = snapshots.GroupBy(s => s.Minute).Select(g => (Minute: g.Key, Queued: g.Sum(s => s.QueuedPassengers))).OrderBy(x => x.Minute).ToArray();
        var peak = queue.MaxBy(x => x.Queued);
        int? cleared = peak.Queued == 0 ? null : queue.Where(x => x.Minute > peak.Minute && x.Queued == 0).Select(x => (int?)x.Minute).FirstOrDefault();
        return new(mean, p95, snapshots.Max(s => s.QueuedPassengers), cleared);
    }

    /// <summary>The actions taken, added up per place: "4 operatori din rezervă la CP-1".</summary>
    public static IReadOnlyList<string> Changes(IEnumerable<AgentOption> picked, IReadOnlyDictionary<Guid, string> names)
    {
        string Name(Guid? id) => id is { } value ? names.GetValueOrDefault(value, "control") : "control";
        var actions = picked.ToArray();
        var changes = new List<string>();
        foreach (var call in actions.Where(o => o.Kind == "staff_call").GroupBy(o => o.ToCheckpointId))
            changes.Add($"{Operators(call.Sum(o => o.Count ?? 0))} din rezervă la {Name(call.Key)}");
        foreach (var move in actions.Where(o => o.Kind == "staff_move").GroupBy(o => (o.FromCheckpointId, o.ToCheckpointId)))
            changes.Add($"{Operators(move.Sum(o => o.Count ?? 0))} mutați de la {Name(move.Key.FromCheckpointId)} la {Name(move.Key.ToCheckpointId)}");
        if (actions.Where(o => o.Kind == "desk_open").Sum(o => o.Count ?? 0) is > 0 and var desks)
            changes.Add(desks == 1 ? "un ghișeu de check-in deschis" : $"{desks} ghișee de check-in deschise");
        foreach (var plan in actions.Where(o => o.Kind == "plan"))
        {
            if (plan.Staff is { Count: > 0 } staff)
                changes.Add("plan optim de operatori: " + string.Join(", ", staff.Select(t => $"{Name(t.CheckpointId)} {t.Target - t.Current:+0;-0}")));
            if (plan.Desks is { Count: > 0 } deskPlan)
                changes.Add($"plan optim de ghișee check-in (între {deskPlan.Min(s => s.StaffCount)} și {deskPlan.Max(s => s.StaffCount)} deschise)");
        }
        foreach (var controlOpening in actions.Where(o => o.Kind == "checkpoint_open"))
            changes.Add($"{Name(controlOpening.ToCheckpointId)} deschis cu un operator și redistribuire de pasageri");
        foreach (var moved in actions.Where(o => o.Kind == "queue_redistribution"))
            changes.Add($"redistribuire din coada de la {Name(moved.FromCheckpointId)} către {Name(moved.ToCheckpointId)}");
        if (actions.Count(o => o.Kind == "gate_choice") is > 0 and var gates)
            changes.Add(gates == 1 ? "o întârziere absorbită prin alegerea porților" : $"{gates} întârzieri absorbite prin alegerea porților");
        if (actions.Count(o => o.Kind == "gate_open") is > 0 and var opened)
            changes.Add(opened == 1 ? "o poartă deschisă de un membru al personalului" : $"{opened} porți deschise de personal");
        return changes;
    }

    private static string Operators(int count) => count == 1 ? "un operator" : $"{count} operatori";
}
