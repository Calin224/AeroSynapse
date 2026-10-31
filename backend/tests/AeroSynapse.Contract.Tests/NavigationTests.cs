using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Airports;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.Airports;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class NavigationTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory,"contracts","navigation","v1");
    private static JsonElement Example(string name) => JsonSerializer.SerializeToElement(JsonNode.Parse(File.ReadAllText(Path.Combine(Root,"examples",name+".json")))!);
    public static IEnumerable<object[]> Cases() => JsonNode.Parse(File.ReadAllText(Path.Combine(Root,"validation-cases.json")))!.AsArray()
        .Select(item => new object[] { item!["name"]!.GetValue<string>(),item.ToJsonString() });
    [Theory]
    [MemberData(nameof(Cases))]
    public void Shared_navigation_case_matches_Python(string name,string caseJson)
    {
        var test=JsonNode.Parse(caseJson)!; Assert.Equal(name,test["name"]!.GetValue<string>());
        var payload=JsonNode.Parse(Example(test["example"]!.GetValue<string>()).GetRawText())!;
        foreach (var mutation in test["mutations"]!.AsArray())
        {
            var parts=mutation!["path"]!.GetValue<string>().Split('/').Skip(1).ToArray(); var parent=payload;
            foreach (var part in parts.SkipLast(1)) parent=parent is JsonArray ? parent[int.Parse(part)]! : parent[part]!;
            if (parent is JsonArray array) array[int.Parse(parts.Last())]=mutation["value"]?.DeepClone(); else parent[parts.Last()]=mutation["value"]?.DeepClone();
        }
        var errors=VersionedContract.Validate(test["definition"]!.GetValue<string>(),JsonSerializer.SerializeToElement(payload));
        Assert.Equal(test["errors"]!.AsArray().Select(item => (item!["field"]!.GetValue<string>(),item["code"]!.GetValue<string>())).OrderBy(item => item.Item1),
            errors.Select(item => (item.Field,item.Code)).OrderBy(item => item.Field));
    }
    [Fact]
    public void Routed_requests_and_responses_roundtrip_and_correlate_with_waypoints_and_boarding_time()
    {
        var request=ContractJson.Read<SimulationRequest>(Example("routed-simulation-request"),"SimulationRequest");
        var response=ContractJson.Read<SimulationResponse>(Example("routed-simulation-response"),"SimulationResponse");
        Assert.True(JsonElement.DeepEquals(Example("routed-simulation-request"),JsonSerializer.SerializeToElement(request,ContractJson.Options)));
        Assert.True(JsonElement.DeepEquals(Example("routed-simulation-response"),JsonSerializer.SerializeToElement(response,ContractJson.Options)));
        Assert.Empty(ContractCorrelation.Validate(request,response));
        Assert.Contains(ContractCorrelation.Validate(request,response with { NavigationResult=null }),item => item.Field=="/navigationResult");
        var report=response.NavigationResult!;
        var invalid=report with { Journeys=[report.Journeys[0] with { GateArrivalMinute=2 },report.Journeys[1]] };
        Assert.Contains(ContractCorrelation.Validate(request,response with { NavigationResult=invalid }),item => item.Field=="/navigationResult");
        var closed=request.Navigation! with { ClosedZoneIds=[report.Routes[0].ZoneIds[1]] };
        Assert.False(NavigationCorrelation.Valid(request.PassengerGeneration!,closed,report,true,0,100000));
    }
    [Fact]
    public void US005_import_preserves_edges_directions_coordinates_and_inactive_floors_gates_and_accessibility()
    {
        var airport=Guid.NewGuid(); var terminal=Guid.NewGuid(); var floor=Guid.NewGuid(); var security=Guid.NewGuid(); var stairs=Guid.NewGuid(); var gate=Guid.NewGuid();
        var map=new AirportMap
        {
            Airport=new() { Id=airport }, Terminals=[new() { Id=terminal }], Floors=[new() { Id=floor }],
            Zones=[new() { Id=security,Name="Security",Type=ZoneType.Security,TerminalId=terminal,FloorId=floor },
                   new() { Id=stairs,Name="Stairs",Type=ZoneType.Stairs,TerminalId=terminal,FloorId=floor,X=3,Y=4 },
                   new() { Id=gate,Name="Gate",Type=ZoneType.Gate,TerminalId=terminal,FloorId=floor,X=6,Y=8 }],
            Checkpoints=[new() { Id=Guid.NewGuid(),ZoneId=security }], Gates=[new() { Id=Guid.NewGuid(),ZoneId=gate }],
            Routes=[new() { Id=Guid.NewGuid(),FromZoneId=security,ToZoneId=stairs,TravelDurationSeconds=30,IsBidirectional=false }]
        };
        var imported=NavigationGraphImporter.FromMap(map);
        Assert.Equal(5,imported.Graph.Edges[0].DistanceMeters); Assert.Equal(30,imported.Graph.Edges[0].TravelSeconds);
        Assert.False(imported.Graph.Edges[0].IsBidirectional); Assert.False(imported.Graph.Edges[0].IsStepFree);
        Assert.Equal(map.Gates[0].Id,imported.GateBindings[0].ResourceId); Assert.Equal(gate,imported.GateBindings[0].ZoneId);
        map.Gates[0].IsActive=false;
        Assert.False(NavigationGraphImporter.FromMap(map).Graph.Nodes.Single(item => item.Id==gate).IsActive);
        map.Floors[0].IsActive=false;
        Assert.All(NavigationGraphImporter.FromMap(map).Graph.Nodes,item => Assert.False(item.IsActive));
    }
}
