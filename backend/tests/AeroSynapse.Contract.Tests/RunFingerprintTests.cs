using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using Xunit;

namespace AeroSynapse.Contract.Tests;

public sealed class RunFingerprintTests
{
    [Fact]
    public void Shared_golden_vectors_match_the_python_implementation()
    {
        using var vectors = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts/reproducibility/v1/fingerprints.json")));
        foreach (var row in vectors.RootElement.EnumerateArray())
            Assert.Equal(row.GetProperty("expected").GetString(), RunFingerprint.Hash(row.GetProperty("value"), row.GetProperty("kind").GetString()!));
    }

    [Fact]
    public void Effective_inputs_ignore_run_identity_but_include_seed_version_and_simulation_parameters()
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "contracts/v1/examples/simulation-request.json")));
        var request = ContractJson.Read<SimulationRequest>(document.RootElement, "SimulationRequest");
        var hash = RunFingerprint.Input(request);
        Assert.Equal(hash, RunFingerprint.Input(request with { RunId = Guid.NewGuid() }));
        Assert.Equal(hash, RunFingerprint.Input(request with { ScenarioId = Guid.NewGuid(), ScenarioRevision = 99 }));
        Assert.NotEqual(hash, RunFingerprint.Input(request with { Seed = request.Seed + 1 }));
        Assert.NotEqual(hash, RunFingerprint.Input(request with { DurationMinutes = request.DurationMinutes + 1 }));
        Assert.NotEqual(hash, RunFingerprint.Input(request with { Engine = "simpy-1.0" }));
    }
}
