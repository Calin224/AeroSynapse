using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using Xunit;

namespace AeroSynapse.Contract.Tests;

// The Python engine runs the same validation corpora; ordered corpora also pin the error order.
public sealed class SharedCorpusTests
{
    private static readonly (string Folder, bool Ordered)[] Corpora =
        [("passengers", false), ("navigation", false), ("kpis", true), ("processes", true), ("events", true)];
    private static string Root(string folder) => Path.Combine(AppContext.BaseDirectory, "contracts", folder, "v1");

    public static IEnumerable<object[]> Cases() => Corpora.SelectMany(corpus =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(Root(corpus.Folder), "validation-cases.json")))!.AsArray()
            .Select(item => new object[] { corpus.Folder, item!["name"]!.GetValue<string>(), item.ToJsonString() }));

    [Theory]
    [MemberData(nameof(Cases))]
    public void Shared_validation_case_matches_Python(string folder, string name, string caseJson)
    {
        var test = JsonNode.Parse(caseJson)!;
        Assert.Equal(name, test["name"]!.GetValue<string>());
        var payload = Apply(JsonNode.Parse(File.ReadAllText(Path.Combine(Root(folder), "examples", test["example"]!.GetValue<string>() + ".json")))!, test["mutations"]!.AsArray());
        var errors = VersionedContract.Validate(test["definition"]!.GetValue<string>(), JsonSerializer.SerializeToElement(payload))
            .Select(item => (item.Field, item.Code));
        var expected = test["errors"]!.AsArray().Select(item => (item!["field"]!.GetValue<string>(), item["code"]!.GetValue<string>()));
        if (Corpora.Single(corpus => corpus.Folder == folder).Ordered) Assert.Equal(expected, errors);
        else Assert.Equal(expected.OrderBy(item => item.Item1), errors.OrderBy(item => item.Field));
    }

    /// <summary>Applies JSON-pointer mutations ("op": "remove", or replace by default) in place.</summary>
    internal static JsonNode Apply(JsonNode payload, JsonArray mutations)
    {
        foreach (var mutation in mutations)
        {
            var parts = mutation!["path"]!.GetValue<string>().Split('/').Skip(1).ToArray();
            var parent = payload;
            foreach (var part in parts.SkipLast(1)) parent = parent is JsonArray ? parent[int.Parse(part)]! : parent[part]!;
            if (mutation["op"]?.GetValue<string>() == "remove") parent.AsObject().Remove(parts[^1]);
            else if (parent is JsonArray array) array[int.Parse(parts[^1])] = mutation["value"]?.DeepClone();
            else parent[parts[^1]] = mutation["value"]?.DeepClone();
        }
        return payload;
    }
}
