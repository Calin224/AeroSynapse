using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

/// <summary>Operators the user entered: security staff at the controls and check-in desk staff.</summary>
public static class OperatorScenarios
{
    public static readonly IReadOnlySet<string> Roles = new HashSet<string>(StringComparer.Ordinal) { "security", "checkIn" };

    public static void Validate(ScenarioInputs inputs, SceneDetails layout)
    {
        if (inputs.Operators is not { } operators) return;
        var errors = new Dictionary<string, string[]>();
        var staff = layout.Staff.Select(member => member.Id).ToHashSet();
        var seen = new HashSet<Guid>();
        foreach (var (person, index) in operators.Select((person, index) => (person, index)))
        {
            var field = $"inputs.operators[{index}]";
            if (person is null) { errors[field] = ["Operator lipsă."]; continue; }
            if (!seen.Add(person.Id)) errors[field + ".id"] = ["Fiecare operator apare o singură dată."];
            if (string.IsNullOrWhiteSpace(person.Name) || person.Name.Length > 60) errors[field + ".name"] = ["Numele are între 1 și 60 de caractere."];
            if (!Roles.Contains(person.Role)) errors[field + ".role"] = ["Specializarea este security sau checkIn."];
            else if (person.Role == "security" && !staff.Contains(person.Id))
                errors[field + ".id"] = ["Toate posturile de securitate din acest aeroport sunt ocupate."];
        }
        var desks = operators.Count(person => person?.Role == "checkIn");
        if (desks > 0 && inputs.CheckIn is null) errors["inputs.checkIn"] = ["Activează check-in-ul pentru operatorii de check-in."];
        else if (inputs.CheckIn is { } checkIn && checkIn.StaffCount != desks)
            errors["inputs.checkIn.staffCount"] = ["Ghișeele deschise sunt numărul operatorilor de check-in."];
        if (errors.Count != 0) throw new ScenarioValidationException(errors);
    }

    /// <summary>The team with exactly <paramref name="desks"/> check-in operators: the first ones stay, new ones get default names.</summary>
    public static IReadOnlyList<ScenarioOperator>? WithDesks(IReadOnlyList<ScenarioOperator>? team, int desks)
    {
        if (team is null) return null;
        var checkIn = team.Where(person => person.Role == "checkIn").ToArray();
        return team.Where(person => person.Role != "checkIn").Concat(checkIn.Take(desks))
            .Concat(Enumerable.Range(checkIn.Length + 1, Math.Max(0, desks - checkIn.Length))
                .Select(number => new ScenarioOperator(Guid.NewGuid(), $"Operator check-in {number}", "checkIn"))).ToArray();
    }
}
