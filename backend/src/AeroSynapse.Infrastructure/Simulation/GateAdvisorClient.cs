using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;

namespace AeroSynapse.Infrastructure.Simulation;

public sealed class GateAdvisorClient(HttpClient http) : IGateAdvisor
{
    public async Task<GateAdvice> AdviseAsync(GateAdviceRequest request, CancellationToken token)
    {
        try
        {
            using var response = await http.PostAsJsonAsync("optimization/gates/v1/advice", request, ContractJson.Options, token);
            if (response.StatusCode == HttpStatusCode.UnprocessableEntity)
            {
                var fields = await response.Content.ReadFromJsonAsync<ContractErrorResponse>(ContractJson.Options, token);
                if (fields is { Errors.Count: > 0 }) throw new ContractValidationException(fields.Errors);
            }
            if (response.StatusCode == HttpStatusCode.Conflict)
            {
                using var conflict = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
                throw new ScenarioConflictException(conflict.RootElement.GetProperty("code").GetString() == "event_conflict"
                    ? "The flight has already departed at that minute; a delay cannot change it."
                    : "No gate plan absorbs this delay within the planning horizon.");
            }
            response.EnsureSuccessStatusCode();
            using var json = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(token), cancellationToken: token);
            GateAdvice advice;
            try { advice = ContractJson.Read<GateAdvice>(json.RootElement, "GateAdvice"); }
            catch (ContractValidationException failure) { throw new JsonException("Invalid gate advice.", failure); }
            if (!Valid(request, advice)) throw new JsonException("Gate advice contradicts the request.");
            return advice;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception failure) when (failure is JsonException or HttpRequestException or OperationCanceledException
            or KeyNotFoundException or InvalidOperationException)
        { throw new GateAdvisorUnavailableException(); }
    }

    /// <summary>Independent checks: every flight once, eligible gates, no shared gate at once, and the reported delay applied.</summary>
    internal static bool Valid(GateAdviceRequest request, GateAdvice advice)
    {
        const double Tolerance = 1e-6;
        var flights = request.Flights.ToDictionary(f => f.FlightId);
        bool Complete(IReadOnlyList<JourneyFlightPlan> plan) =>
            plan.Count == flights.Count && plan.Select(p => p.FlightId).ToHashSet().SetEquals(flights.Keys);
        bool Separated(IReadOnlyList<JourneyFlightPlan> plan) => plan.Where(p => !p.IsCancelled).GroupBy(p => p.GateId).All(gate =>
        {
            var turns = gate.OrderBy(p => p.ArrivalMinute).ToArray();
            return turns.Zip(turns.Skip(1)).All(pair => pair.First.DepartureMinute <= pair.Second.ArrivalMinute + Tolerance);
        });
        if (advice.AdviceVersion != request.AdviceVersion || Math.Abs(advice.Minute - request.Minute) > Tolerance) return false;
        if (!Complete(advice.Before) || !Complete(advice.Recommended) || advice.WithoutMoves is { } unmoved && !Complete(unmoved)) return false;
        var before = advice.Before.ToDictionary(p => p.FlightId);
        var target = advice.Recommended.Single(p => p.FlightId == request.Delay.FlightId);
        return advice.Recommended.All(p => flights[p.FlightId].EligibleGateIds.Contains(p.GateId)) && Separated(advice.Recommended)
            && target.DepartureMinute + Tolerance >= before[target.FlightId].DepartureMinute + request.Delay.DelayMinutes
            // A turnaround already under way keeps its gate and arrival.
            && advice.Recommended.All(p => before[p.FlightId].ArrivalMinute >= request.Minute
                || p.GateId == before[p.FlightId].GateId && Math.Abs(p.ArrivalMinute - before[p.FlightId].ArrivalMinute) <= Tolerance)
            && (advice.WithoutMoves is null
                || Separated(advice.WithoutMoves) && advice.WithoutMoves.All(p => p.GateId == before[p.FlightId].GateId));
    }
}
