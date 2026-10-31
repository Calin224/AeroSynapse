using System.Security.Cryptography;
using System.Text.Json;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using Microsoft.AspNetCore.DataProtection;

namespace AeroSynapse.API;

// Authenticated, encrypted receipts bind a transcript to the actual server response and its scope.
// Generating an explanation never writes a transcript or runs an additional tool.
internal sealed class CopilotReceipts(IDataProtectionProvider protection, TimeProvider clock)
{
    private IDataProtector Protector => protection.CreateProtector("AeroSynapse.Copilot.Transcripts.v1", AeroSynapse.Infrastructure.Governance.TenantContext.Id);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public CopilotAnswer Attach(CopilotAnswer answer, string question)
    {
        var explanation = CopilotExplanationBuilder.Answer(answer);
        return answer with { Explanation = explanation, Receipt = Create("answer", answer.Scope, question, explanation) };
    }
    public CopilotToolResult Attach(CopilotToolResult result)
    {
        var explanation = CopilotExplanationBuilder.Tool(result);
        return result with { Explanation = explanation, Receipt = Create(result.Tool, result.Scope, null, explanation) };
    }
    private string Create(string kind, CopilotScope scope, string? question, CopilotExplanation explanation) =>
        Protector.Protect(JsonSerializer.Serialize(new CopilotTranscriptCapture(Guid.NewGuid(), kind, scope,
            clock.GetUtcNow().UtcDateTime.AddMinutes(15), question, explanation), Json));
    public CopilotTranscriptCapture Read(string receipt, Guid scenarioId, Guid runId)
    {
        if (string.IsNullOrWhiteSpace(receipt) || receipt.Length > 1_048_576) throw Invalid();
        CopilotTranscriptCapture capture;
        try { capture = JsonSerializer.Deserialize<CopilotTranscriptCapture>(Protector.Unprotect(receipt), Json) ?? throw Invalid(); }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException) { throw Invalid(); }
        if (capture.Scope.ScenarioId != scenarioId || capture.Scope.RunId != runId
            || capture.ReceiptExpiresAtUtc <= clock.GetUtcNow().UtcDateTime) throw Invalid();
        return capture;
    }
    private static ScenarioValidationException Invalid() => new(new Dictionary<string, string[]> {
        ["receipt"] = ["Receipt is invalid, expired, or belongs to a different scenario/run. Request a new explanation."] });
}
