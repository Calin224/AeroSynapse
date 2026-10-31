using System.Text.Json;
using System.Text.Json.Nodes;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;
namespace AeroSynapse.Contract.Tests;
public sealed class KpiTests
{
    private static readonly string Root=Path.Combine(AppContext.BaseDirectory,"contracts","kpis","v1");
    private static JsonElement Example(string name)=>JsonSerializer.SerializeToElement(JsonNode.Parse(File.ReadAllText(Path.Combine(Root,"examples",name+".json")))!);
    [Theory][InlineData("kpi-request","kpi-response")][InlineData("empty-request","empty-response")][InlineData("check-in-request","check-in-response")][InlineData("check-in-events-request","check-in-events-response")]
    public void Request_and_response_roundtrip_and_correlate_with_observed_processes(string input,string output)
    {
        var request=ContractJson.Read<SimulationRequest>(Example(input),"SimulationRequest");
        var response=ContractJson.Read<SimulationResponse>(Example(output),"SimulationResponse");
        Assert.True(JsonElement.DeepEquals(Example(input),JsonSerializer.SerializeToElement(request,ContractJson.Options)));
        Assert.True(JsonElement.DeepEquals(Example(output),JsonSerializer.SerializeToElement(response,ContractJson.Options)));
        Assert.Empty(ContractCorrelation.Validate(request,response));
        Assert.Contains(ContractCorrelation.Validate(request,response with { KpiResult=null }),item=>item.Field=="/kpiResult");
    }
    [Fact]
    public void Alternative_flows_accept_the_stage_engine_aggregates_without_a_single_control_kpi_report()
    {
        var request=ContractJson.Read<SimulationRequest>(Example("kpi-request"),"SimulationRequest");
        var flow=new PassengerFlowConfig([new("standard",1,false,"security",1,1)],[],[]);
        request=request with { Navigation=null, PassengerGeneration=request.PassengerGeneration! with { Flow=flow } };
        var response=ContractJson.Read<SimulationResponse>(Example("kpi-response"),"SimulationResponse")
            with { KpiResult=null, ProcessResult=null, NavigationResult=null };
        Assert.Empty(ContractCorrelation.Validate(request,response));
        var standard=request with { PassengerGeneration=request.PassengerGeneration! with { Flow=null } };
        Assert.Contains(ContractCorrelation.Validate(standard,response),item=>item.Field=="/kpiResult");
    }
    [Fact]
    public void Quantiles_are_recomputed_from_completed_services_and_false_wait_values_are_rejected()
    {
        var request=ContractJson.Read<SimulationRequest>(Example("kpi-request"),"SimulationRequest");
        var response=ContractJson.Read<SimulationResponse>(Example("kpi-response"),"SimulationResponse");
        var report=response.KpiResult!;
        var changed=report with { Snapshots=report.Snapshots.Select(row=>row.Minute==2 ? row with { P95WaitMinutes=.7 } : row).ToArray() };
        Assert.False(KpiCorrelation.Valid(request,response with { KpiResult=changed }));
        var first=report.Snapshots[0]; Assert.Null(first.MeanWaitMinutes); Assert.Equal(0,first.WaitSampleCount);
        var final=report.Snapshots[^1]; Assert.Equal(.5,final.MedianWaitMinutes); Assert.Equal(1,final.P95WaitMinutes); Assert.Equal(.2,final.CumulativeUtilization);
    }
}
