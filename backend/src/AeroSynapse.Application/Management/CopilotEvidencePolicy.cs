using System.Globalization;
using System.Text.RegularExpressions;

namespace AeroSynapse.Application.Management;

// A last gate on the deterministic renderer. No numeric claim is accepted just
// because a language model supplied it, and a missing citation cannot pass.
public static partial class CopilotEvidencePolicy
{
    public static bool Accepts(CopilotAnswer answer)
    {
        if (answer.Citations.Any(c => !double.IsFinite(c.Value) || c.RunId != answer.Scope.RunId
            || c.ScenarioId != answer.Scope.ScenarioId || c.ZoneId != answer.Scope.ZoneId || c.Minute != answer.Scope.Minute)) return false;
        var numbers = answer.Citations.Select(c => c.Value.ToString("0.##########", CultureInfo.InvariantCulture)).ToHashSet();
        // Units can contain their own documented domain, e.g. "fracție [0,1]".
        // Remove only exact cited units before checking the numeric prose.
        var prose = answer.Citations.Select(c => c.Unit).Distinct().Where(unit => !string.IsNullOrEmpty(unit))
            .Aggregate(answer.Answer, (text, unit) => text.Replace(unit, "", StringComparison.Ordinal));
        return NumericClaims().Matches(prose).All(m => numbers.Contains(m.Value));
    }
    [GeneratedRegex(@"(?<![\p{L}\d])[-+]?\d+(?:\.\d+)?(?![\p{L}\d])", RegexOptions.CultureInvariant)]
    private static partial Regex NumericClaims();
}
