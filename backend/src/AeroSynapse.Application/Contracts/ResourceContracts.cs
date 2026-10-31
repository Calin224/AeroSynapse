using AeroSynapse.Domain.DigitalTwin;
using System.Text.Json;

namespace AeroSynapse.Application.Contracts;

public sealed record ResourceFlight(Guid FlightId, Guid CurrentGateId, double StartMinute, double EndMinute);
public sealed record ResourceSolveRequest(string ContractVersion, Guid ScenarioId, int ScenarioRevision, int DurationMinutes,
    double TimeLimitSeconds, IntegratedResourceConfig Resources, IReadOnlyList<ResourceFlight> Flights);
public sealed record ResourceAssignment(Guid FlightId, Guid GateId, Guid TeamId);
public sealed record ResourceRule(string Code, Guid? FlightId, Guid? ResourceId, string Message);
public sealed record ResourceSolveResult(string Status, string SolverStatus, bool TimedOut, int? Score, int? BestBound,
    IReadOnlyList<ResourceAssignment> Assignments, IReadOnlyList<ResourceRule> Rules, ResourceSolveRequest Constraints,
    bool AdvisoryOnly, double WallTimeSeconds);
public interface IResourcePlanner
{
    Task<ResourceSolveResult> SolveAsync(ResourceSolveRequest request, CancellationToken token);
}
public sealed class ResourcePlannerUnavailableException() : Exception("The integrated resource solver is unavailable or returned an invalid proposal.");

public static class ResourceValidation
{
    public static bool Covers(IEnumerable<ResourceWindow> windows, double start, double end)
    {
        // Adjacent windows form continuous availability. A gap of any size is unavailable.
        var cursor = start;
        foreach (var window in windows.OrderBy(item => item.StartMinute))
        {
            if (window.EndMinute <= cursor) continue;
            if (window.StartMinute > cursor) return false;
            cursor = window.EndMinute;
            if (cursor >= end) return true;
        }
        return false;
    }

    public static void Config(IntegratedResourceConfig config, string path, List<ContractFieldError> errors)
    {
        void Error(string field, string code = "invalid_value") => errors.Add(VersionedContract.Error(path + field, code));
        void Unique<T>(IEnumerable<T> values, string field) { if (values.Distinct().Count() != values.Count()) Error(field, "duplicate_id"); }
        Unique(config.Gates.Select(item => item.GateId), "/gates");
        Unique(config.Workers.Select(item => item.StaffMemberId), "/workers");
        Unique(config.Teams.Select(item => item.TeamId), "/teams");
        Unique(config.Requirements.Select(item => item.FlightId), "/requirements");
        var gates = config.Gates.Select(item => item.GateId).ToHashSet();
        var workers = config.Workers.Select(item => item.StaffMemberId).ToHashSet();
        foreach (var gate in config.Gates)
            foreach (var window in gate.Availability) if (window.StartMinute >= window.EndMinute) Error("/gates/availability");
        foreach (var worker in config.Workers)
        {
            Unique(worker.Skills, "/workers/skills");
            foreach (var window in worker.Shifts) if (window.StartMinute >= window.EndMinute) Error("/workers/shifts");
        }
        foreach (var team in config.Teams)
        {
            Unique(team.MemberIds, "/teams/memberIds"); Unique(team.GateIds, "/teams/gateIds");
            if (team.MemberIds.Any(id => !workers.Contains(id)) || team.GateIds.Any(id => !gates.Contains(id))) Error("/teams");
        }
        foreach (var requirement in config.Requirements)
        {
            Unique(requirement.CompatibleGateIds, "/requirements/compatibleGateIds"); Unique(requirement.RequiredSkills, "/requirements/requiredSkills");
            if (requirement.CompatibleGateIds.Any(id => !gates.Contains(id))) Error("/requirements/compatibleGateIds");
        }
    }

    public static void Request(ResourceSolveRequest request, string path, List<ContractFieldError> errors)
    {
        Config(request.Resources, path + "/resources", errors);
        var ids = request.Flights.Select(item => item.FlightId).ToArray();
        if (ids.Distinct().Count() != ids.Length) errors.Add(VersionedContract.Error(path + "/flights", "duplicate_id"));
        if (!ids.ToHashSet().SetEquals(request.Resources.Requirements.Select(item => item.FlightId)))
            errors.Add(VersionedContract.Error(path + "/resources/requirements", "invalid_value"));
        if (request.Flights.Any(item => item.StartMinute >= item.EndMinute || item.EndMinute > request.DurationMinutes)
            || request.Resources.Gates.SelectMany(item => item.Availability).Concat(request.Resources.Workers.SelectMany(item => item.Shifts))
                .Any(item => item.EndMinute > request.DurationMinutes)) errors.Add(VersionedContract.Error(path, "out_of_range"));
    }

    public static bool Eligible(ResourceSolveRequest request, ResourceAssignment assignment)
    {
        var flight = request.Flights.SingleOrDefault(item => item.FlightId == assignment.FlightId);
        var gate = request.Resources.Gates.SingleOrDefault(item => item.GateId == assignment.GateId);
        var team = request.Resources.Teams.SingleOrDefault(item => item.TeamId == assignment.TeamId);
        var requirement = request.Resources.Requirements.SingleOrDefault(item => item.FlightId == assignment.FlightId);
        if (flight is null || gate is null || team is null || requirement is null) return false;
        var workers = team.MemberIds.Select(id => request.Resources.Workers.Single(item => item.StaffMemberId == id)).ToArray();
        return requirement.CompatibleGateIds.Contains(gate.GateId) && team.GateIds.Contains(gate.GateId)
            && Covers(gate.Availability, flight.StartMinute, flight.EndMinute)
            && workers.Length >= requirement.MinimumTeamSize
            && requirement.RequiredSkills.All(skill => workers.Any(worker => worker.Skills.Contains(skill)))
            && workers.All(worker => Covers(worker.Shifts, flight.StartMinute, flight.EndMinute));
    }

    public static bool Valid(ResourceSolveRequest request, ResourceSolveResult result)
    {
        if (JsonSerializer.Serialize(request, ContractJson.Options) != JsonSerializer.Serialize(result.Constraints, ContractJson.Options)
            || !result.AdvisoryOnly) return false;
        var status = result.SolverStatus switch { "OPTIMAL" => "optimal", "FEASIBLE" => "feasible", "INFEASIBLE" => "infeasible",
            "UNKNOWN" => "timeout", "MODEL_INVALID" => "invalid_model", _ => "" };
        if (result.Status != status || result.TimedOut != (status is "feasible" or "timeout")) return false;
        var flightIds = request.Flights.Select(item => item.FlightId).ToHashSet();
        var gateIds = request.Resources.Gates.Select(item => item.GateId).ToHashSet();
        var teamIds = request.Resources.Teams.Select(item => item.TeamId).ToHashSet();
        var workerIds = request.Resources.Workers.Select(item => item.StaffMemberId).ToHashSet();
        foreach (var rule in result.Rules)
        {
            var valid = rule.Code switch
            {
                "gate_coverage" or "handling_coverage" => rule.FlightId is { } fid && flightIds.Contains(fid) && rule.ResourceId is null,
                "gate_capacity" => rule.FlightId is null && rule.ResourceId is { } gid && gateIds.Contains(gid),
                "team_capacity" => rule.FlightId is null && rule.ResourceId is { } tid && teamIds.Contains(tid),
                "staff_capacity" => rule.FlightId is null && rule.ResourceId is { } wid && workerIds.Contains(wid),
                "gate_availability" or "gate_compatibility" => rule.FlightId is { } fid && flightIds.Contains(fid) && rule.ResourceId is { } gid && gateIds.Contains(gid),
                "team_skills" or "team_shift" or "team_size" or "team_gate_compatibility" => rule.FlightId is { } fid && flightIds.Contains(fid) && rule.ResourceId is { } tid && teamIds.Contains(tid),
                _ => false
            };
            if (!valid) return false;
        }
        if (status is not ("optimal" or "feasible"))
            return result.Assignments.Count == 0 && result.Score is null && result.BestBound is null
                && (status == "infeasible" ? result.Rules.Count > 0 : result.Rules.Count == 0);
        if (result.Rules.Count > 0 || result.Assignments.Count != request.Flights.Count
            || !result.Assignments.Select(item => item.FlightId).ToHashSet().SetEquals(request.Flights.Select(item => item.FlightId))
            || result.Assignments.Any(item => !Eligible(request, item))) return false;
        var flights = request.Flights.ToDictionary(item => item.FlightId);
        var teams = request.Resources.Teams.ToDictionary(item => item.TeamId);
        for (var i = 0; i < result.Assignments.Count; i++)
            for (var j = i + 1; j < result.Assignments.Count; j++)
            {
                var a = result.Assignments[i]; var b = result.Assignments[j];
                var x = flights[a.FlightId]; var y = flights[b.FlightId];
                if (x.StartMinute >= y.EndMinute || y.StartMinute >= x.EndMinute) continue;
                if (a.GateId == b.GateId || a.TeamId == b.TeamId || teams[a.TeamId].MemberIds.Intersect(teams[b.TeamId].MemberIds).Any()) return false;
            }
        var score = result.Assignments.Count(item => item.GateId != flights[item.FlightId].CurrentGateId);
        return result.Score == score && result.BestBound is >= 0 && result.BestBound <= score
            && (status != "optimal" || result.BestBound == score);
    }
}
