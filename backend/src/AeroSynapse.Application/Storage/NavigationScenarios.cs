using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

public static class NavigationScenarios
{
    public static Guid StableId(string purpose, Guid scene) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"us020/v1/{scene}/{purpose}"))[..16]);
    public static NavigationConfig Defaults(SceneDetails layout)
    {
        var nodes = new List<NavigationNode>(); var edges = new List<NavigationEdge>();
        var corridor = StableId("main-corridor",layout.Scene.Id); var alternative = StableId("alternative-corridor",layout.Scene.Id);
        nodes.Add(new(corridor,"Coridor principal · DEMO","Walkway",-45,0,27,true,true,[]));
        nodes.Add(new(alternative,"Coridor alternativ · DEMO","Walkway",-45,0,17,true,true,[]));
        var commonTime = layout.Routes.Select(route => (double)route.TravelSeconds).DefaultIfEmpty(60).Min() / 2;
        var commonDistance = layout.Routes.Select(route => (double)route.DistanceMeters).DefaultIfEmpty(60).Min() / 2;
        var index = 0;
        foreach (var checkpoint in layout.Checkpoints.OrderBy(item => item.Code,StringComparer.Ordinal))
        {
            nodes.Add(new(checkpoint.Id,checkpoint.Name,"Security",0,0,27+index++*12,true,true,[]));
            var links = layout.Routes.Where(route => route.FromCheckpointId == checkpoint.Id).ToArray();
            if (links.Length == 0) continue;
            var time = links.Min(route => route.TravelSeconds) - commonTime;
            var distance = (double)links.Min(route => route.DistanceMeters) - commonDistance;
            edges.Add(new(StableId($"{checkpoint.Id}/main",layout.Scene.Id),checkpoint.Id,corridor,distance,time,false,true,true,[]));
            edges.Add(new(StableId($"{checkpoint.Id}/alternative",layout.Scene.Id),checkpoint.Id,alternative,distance*1.5,time*1.5,false,true,true,[]));
        }
        index = 0;
        foreach (var gate in layout.Gates.OrderBy(item => item.Code,StringComparer.Ordinal))
        {
            nodes.Add(new(gate.Id,gate.Name,"Gate",-80+index++*14,0,47,true,true,[]));
            var links = layout.Routes.Where(route => route.ToGateId == gate.Id).ToArray();
            if (links.Length == 0) continue;
            var time = links.Min(route => route.TravelSeconds) - commonTime;
            var distance = (double)links.Min(route => route.DistanceMeters) - commonDistance;
            edges.Add(new(StableId($"main/{gate.Id}",layout.Scene.Id),corridor,gate.Id,distance,time,false,true,true,[]));
            edges.Add(new(StableId($"alternative/{gate.Id}",layout.Scene.Id),alternative,gate.Id,distance*1.5,time*1.5,false,true,true,[]));
        }
        return new("1.0",new(nodes,edges),layout.Checkpoints.Select(item => new NavigationBinding(item.Id,item.Id)).ToArray(),
            layout.Gates.Select(item => new NavigationBinding(item.Id,item.Id)).ToArray(),
            new[] { "solo","family","business","tour" }.Select(kind => new NavigationAccessProfile(kind,false,[],[],1)).ToArray(),[],[]);
    }

    public static void Validate(ScenarioInputs inputs, SceneDetails layout)
    {
        if (inputs.Navigation is null) return;
        if (inputs.PassengerGeneration is null) throw Invalid("", "Activate passenger profiles before configuring navigation.");
        Shape(inputs.Navigation);
        foreach (var binding in inputs.Navigation.CheckpointBindings)
            if (!layout.Checkpoints.Any(item => item.Id == binding.ResourceId)) throw Invalid("checkpointBindings","Bind only checkpoints in the captured operational layout.");
        foreach (var binding in inputs.Navigation.GateBindings)
            if (!layout.Gates.Any(item => item.Id == binding.ResourceId)) throw Invalid("gateBindings","Bind only gates in the captured operational layout.");
    }

    public static NavigationInput Input(ScenarioInputs inputs, SceneDetails layout)
    {
        Validate(inputs,layout);
        var config = inputs.Navigation ?? throw Invalid("","Configure navigation first.");
        return new(config.NavigationVersion,config.Graph,config.CheckpointBindings,config.GateBindings,config.AccessProfiles,
            config.ClosedZoneIds,config.ClosedEdgeIds,inputs.Flights.Select(flight => new NavigationFlightGate(flight.Id,flight.GateId)).ToArray());
    }

    public static NavigationConfig Shape(NavigationConfig config)
    {
        try { return ContractJson.Read<NavigationConfig>(JsonSerializer.SerializeToElement(config,ContractJson.Options),"NavigationConfig"); }
        catch (ContractValidationException failure)
        {
            throw new ScenarioValidationException(failure.Errors.GroupBy(error => "inputs.navigation"+error.Field.Replace('/','.'))
                .ToDictionary(group => group.Key,group => group.Select(error => error.Message).ToArray()));
        }
    }
    private static ScenarioValidationException Invalid(string field,string message) => new(new Dictionary<string,string[]>
        { ["inputs.navigation"+(field.Length == 0 ? "" : "."+field)] = [message] });
}
