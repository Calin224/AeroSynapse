using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

// The same corpus is executed by pytest against both deployed HTTP runtimes.
public sealed class CriticalCorpusTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts");
    public static IEnumerable<object[]> Cases() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root, "critical", "v1", "cases.json")))!.AsArray()
        .Select(row => new object[] { row!["name"]!.GetValue<string>(), row.ToJsonString() });

    [Theory]
    [MemberData(nameof(Cases))]
    public void Reviewed_minimal_and_infeasible_corpus_has_the_same_contract_decision(string name, string serialized)
    {
        var test = JsonNode.Parse(serialized)!;
        Assert.Equal(name, test["name"]!.GetValue<string>());
        var value = JsonNode.Parse(File.ReadAllText(Path.Combine(Root, test["fixture"]!.GetValue<string>())))!;
        foreach (var mutation in test["mutations"]!.AsArray())
        {
            var path = mutation!["path"]!.GetValue<string>().Split('/').Skip(1).ToArray(); var parent = value;
            foreach (var key in path.SkipLast(1)) parent = parent is JsonArray array ? array[int.Parse(key)]! : parent[key]!;
            if (parent is JsonArray list) list[int.Parse(path[^1])] = mutation["value"]?.DeepClone();
            else parent[path[^1]] = mutation["value"]?.DeepClone();
        }
        var json = JsonSerializer.SerializeToElement(value);
        var definition = test["definition"]!.GetValue<string>();
        var errors = VersionedContract.Validate(definition, json);
        if (test["status"]!.GetValue<int>() != 200)
        {
            Assert.NotEmpty(errors); Assert.All(errors, e => { Assert.NotEmpty(e.Code); Assert.StartsWith("/", e.Field); });
            return;
        }
        Assert.Empty(errors);
        var roundTrip = definition == "StaffingSolveRequest"
            ? JsonSerializer.SerializeToElement(ContractJson.Read<StaffingSolveRequest>(json, definition), ContractJson.Options)
            : JsonSerializer.SerializeToElement(ContractJson.Read<SimulationRequest>(json, definition), ContractJson.Options);
        Assert.Empty(VersionedContract.Validate(definition, roundTrip));
    }
}
