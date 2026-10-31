using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class StaffingTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "staffing", "v1", "examples");
    private static JsonElement Example(string name) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(Root, name + ".json")));

    [Fact]
    public void Python_optimum_roundtrips_and_the_configuration_capacity_and_score_reconcile()
    {
        var request = ContractJson.Read<StaffingSolveRequest>(Example("staffing-request"), "StaffingSolveRequest");
        var result = ContractJson.Read<StaffingSolveResult>(Example("staffing-response"), "StaffingSolveResult");
        Assert.True(StaffingValidation.Valid(request, result));
        Assert.Equal(40, result.Score); Assert.Equal(new[] { 2, 1 }, result.Assignments.Select(item => item.StaffCount));
        using var mathematical = JsonDocument.Parse(Example("staffing-response").GetRawText().Replace("\"score\": 40", "\"score\": 40.0", StringComparison.Ordinal));
        Assert.Equal(40, ContractJson.Read<StaffingSolveResult>(mathematical.RootElement, "StaffingSolveResult").Score);
    }

    [Theory]
    [InlineData("capacity")]
    [InlineData("score")]
    [InlineData("staff")]
    [InlineData("duplicate")]
    [InlineData("timeout")]
    public void Forged_or_inconsistent_solver_output_is_rejected(string kind)
    {
        var response = JsonNode.Parse(Example("staffing-response").GetRawText())!;
        switch (kind)
        {
            case "capacity": response["assignments"]![0]!["estimatedCapacityMilli"] = 999999; break;
            case "score": response["score"] = 0; break;
            case "staff": response["assignments"]![0]!["staffCount"] = 4; break;
            case "duplicate": response["assignments"]![1]!["checkpointId"] = response["assignments"]![0]!["checkpointId"]!.DeepClone(); break;
            case "timeout": response["status"] = "timeout"; response["solverStatus"] = "UNKNOWN"; response["timedOut"] = true; break;
        }
        Assert.Contains(VersionedContract.Validate("StaffingSolveResult", JsonSerializer.SerializeToElement(response)), item => item.Code == "invalid_result");
    }

    [Fact]
    public void Request_ids_and_terminal_pools_are_validated_before_solving()
    {
        var request = JsonNode.Parse(Example("staffing-request").GetRawText())!;
        request["checkpoints"]![1]!["checkpointId"] = request["checkpoints"]![0]!["checkpointId"]!.DeepClone();
        Assert.Contains(VersionedContract.Validate("StaffingSolveRequest", JsonSerializer.SerializeToElement(request)),
            item => item.Field == "/checkpoints/1/checkpointId" && item.Code == "duplicate_id");
    }

    [Fact]
    public void Maximum_profile_capacity_and_staffing_roundtrip_without_integer_overflow()
    {
        var original = ContractJson.Read<StaffingSolveRequest>(Example("staffing-request"), "StaffingSolveRequest");
        var checkpoint = original.Checkpoints[0] with { CurrentStaff = 10000, MinimumStaff = 10000,
            MaximumStaff = 10000, PhysicalLimit = 10000, RequiredOpen = true, CapacityMilli = 10000000000000L };
        var request = original with { AvailableStaff = 10000, Checkpoints = [checkpoint],
            StaffPools = [original.StaffPools[0] with { AvailableStaff = 10000 }] };
        var result = new StaffingSolveResult("optimal", "OPTIMAL", false, 0, 0, 10000, 0,
            [new(checkpoint.CheckpointId, 10000, 100000000000000000L, 0, 0)], request, StaffingValidation.Objective, 1000, 0);
        var parsed = ContractJson.Read<StaffingSolveResult>(JsonSerializer.SerializeToElement(result, ContractJson.Options), "StaffingSolveResult");
        Assert.True(StaffingValidation.Valid(request, parsed));
        Assert.Equal(100000000000000000L, parsed.Assignments[0].EstimatedCapacityMilli);
    }

    [Fact]
    public void Candidate_search_results_are_feasible_distinct_and_exclude_the_baseline()
    {
        var request = ContractJson.Read<StaffingCandidateSearchRequest>(Example("staffing-candidates-request"), "StaffingCandidateSearchRequest");
        var result = ContractJson.Read<StaffingCandidateSearchResult>(Example("staffing-candidates-response"), "StaffingCandidateSearchResult");
        Assert.True(StaffingValidation.ValidCandidates(request, result)); Assert.Equal(4, result.Candidates.Count);
        Assert.False(StaffingValidation.ValidCandidates(request with { CandidateCount = 3 }, result));
        var repeated = result with { Candidates = [result.Candidates[0], result.Candidates[0], result.Candidates[2], result.Candidates[3]] };
        Assert.Contains(VersionedContract.Validate("StaffingCandidateSearchResult", JsonSerializer.SerializeToElement(repeated, ContractJson.Options)),
            item => item.Field == "/candidates/1" && item.Code == "invalid_result");
    }
}
