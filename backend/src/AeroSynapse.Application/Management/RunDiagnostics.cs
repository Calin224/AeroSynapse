using System.Text.RegularExpressions;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Simulation;

namespace AeroSynapse.Application.Management;

public static class RunDiagnostics
{
    public static bool Valid(EngineDiagnostics? diagnostics, SimulationRequest request, SimulationResponse response, string? resultHash = null) => diagnostics is null || (
        diagnostics.InputHash == RunFingerprint.Input(request) && diagnostics.EngineVersion == RunFingerprint.Engine(request)
        && diagnostics.EngineBuild is not null && Regex.IsMatch(diagnostics.EngineBuild, "^[0-9a-f]{64}$")
        && double.IsFinite(diagnostics.EngineMilliseconds) && diagnostics.EngineMilliseconds >= 0
        && diagnostics.ResultHash == (response.Status == "completed" ? resultHash ?? RunFingerprint.Result(response) : null));
}
