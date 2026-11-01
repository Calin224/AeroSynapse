using System.Globalization;
using System.Text.RegularExpressions;

namespace AeroSynapse.Application.Management;

// A last gate on the deterministic renderer. No numeric claim is accepted just
// because a language model supplied it, and a missing citation cannot pass.
public static partial class CopilotEvidencePolicy
{
    /// <param name="places">Checkpoint names the text may use; a digit in "CP-2" names a place, it is not a claim.</param>
    public static bool Accepts(CopilotAnswer answer, IEnumerable<string>? places = null)
    {
        var runFacts = answer.RunFacts ?? [];
        if (answer.Citations.Any(c => !double.IsFinite(c.Value) || c.RunId != answer.Scope.RunId
            || c.ScenarioId != answer.Scope.ScenarioId || c.ZoneId != answer.Scope.ZoneId || c.Minute != answer.Scope.Minute)) return false;
        if (runFacts.Any(f => f.RunId != answer.Scope.RunId || f.Value is not { } value || !double.IsFinite(value))) return false;
        var numbers = answer.Citations.Select(c => c.Value).Concat(runFacts.Select(f => f.Value!.Value))
            .Select(value => value.ToString("0.##########", CultureInfo.InvariantCulture)).ToHashSet();
        // Units can contain their own documented domain, e.g. "fracție [0,1]". Remove only exact cited units,
        // then whole place names, longest first, so neither "CP-1" inside "CP-10" nor a place "1" inside "21" is erased.
        var prose = answer.Citations.Select(c => c.Unit).Concat(runFacts.Select(f => f.Unit))
            .Where(unit => !string.IsNullOrEmpty(unit)).Distinct()
            .Aggregate(answer.Answer, (text, unit) => text.Replace(unit, "", StringComparison.Ordinal));
        prose = (places ?? []).Where(place => !string.IsNullOrEmpty(place)).Distinct().OrderByDescending(place => place.Length)
            .Aggregate(prose, (text, place) => Regex.Replace(text, $@"(?<![\p{{L}}\d]){Regex.Escape(place)}(?![\p{{L}}\d])", ""));
        return NumericClaims().Matches(prose).All(m => numbers.Contains(m.Value));
    }
    [GeneratedRegex(@"(?<![\p{L}\d])[-+]?\d+(?:\.\d+)?(?![\p{L}\d])", RegexOptions.CultureInvariant)]
    private static partial Regex NumericClaims();
}
