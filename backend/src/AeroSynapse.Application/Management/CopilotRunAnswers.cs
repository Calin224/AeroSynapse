using System.Globalization;
using System.Text.RegularExpressions;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;

namespace AeroSynapse.Application.Management;

/// <summary>A whole-run reply. Every number in Text is one of Facts, rounded to a whole number as the text shows it.</summary>
public sealed record CopilotRunReply(string Status, string ReasonCode, string Text, IReadOnlyList<CopilotEvidenceFact> Facts);
/// <summary>A routed whole-run question: its topic, a checkpoint it names, and a minute or queue limit it names.</summary>
public sealed record CopilotRunQuery(string Topic, Guid? ZoneId = null, int? Value = null);

// Questions about the whole run ("where was the longest queue?") read every checkpoint's persisted KPI series,
// not the selected zone and minute. All prose is templated here; no model writes it.
public static class CopilotRunAnswers
{
    /// <summary>Scenario events that can explain a queue; the inspect_zone tool lists the same types.</summary>
    public static readonly IReadOnlyList<string> EventTypes = ["passenger_surge", "checkpoint_unavailable", "staff_reduction", "flight_change", "corridor_closure", "resume"];
    private static readonly Dictionary<string, string> EventNames = new()
    {
        ["passenger_surge"] = "val de pasageri", ["checkpoint_unavailable"] = "control indisponibil", ["staff_reduction"] = "scădere de personal",
        ["flight_change"] = "modificare de zbor", ["corridor_closure"] = "închidere de coridor", ["resume"] = "reluare"
    };
    /// <summary>Suggested questions, all answered from run data; the page's starter buttons ask the same ones.</summary>
    public const string Examples = "„Rezumă simularea”, „Unde a fost cea mai mare coadă și de ce?”, „Câți pasageri au pierdut zborul?”, „Ce ar trebui să schimb?”";

    // Matched as substrings of " text in lowercase without diacritics or punctuation ", so " e " is a whole word.
    private static readonly string[] Unanswerable = [" daca ", "what if", "predict", "prognoz", "forecast", " va fi ", " vor fi ", " maine ", "tomorrow", " will ", " would "];
    // The selected zone and minute keep the per-zone answer.
    private static readonly string[] Moment = [" acum ", " aici ", " in acest moment", " la acest minut", " minutul ales", " momentul ales", " zona aleasa", " zona selectata",
        " zona asta ", " aceasta zona", " acestei zone", " zonei ", " rezuma zona", " this zone", " right now", " selected zone"];
    private static readonly string[] Compare = ["compar", "diferent", " fata de ", "anterioar", "precedent", "previous", "difference", " versus ", " vs "];
    private static readonly string[] Advice = [" ar trebui", " ce sa schimb", " ce schimb", " ce pot schimba", "imbunatat", " reduc", "optimiz", "recoman", " sfat",
        " ce sa fac", " ce pot face", " what should", " how can i", "improve", " what to change"];
    private static readonly string[] Missed = ["pierdut", " ratat", "missed", "intarzi", " late ", " nu au prins"];
    private static readonly string[] ByFlight = [" ce zbor", " care zbor", " fiecare zbor", " pe zbor", " zboruri", "which flight", " per flight", " each flight"];
    private static readonly string[] Calm = [" cel mai liber", " cea mai libera", " cea mai mica coad", " cea mai scurta coad", " cel mai putin", " cea mai putin",
        " least busy", " quietest", " shortest queue"];
    private static readonly string[] Peak = [" cea mai mare coad", " cea mai lunga coad", " cele mai mari coz", " cele mai lungi coz", " cel mai aglomerat", " cea mai aglomerat",
        " cel mai rau", " cel mai prost", " varf", "aglomer", " blocaj", " biggest queue", " longest queue", " busiest", " worst", " bottleneck", " peak"];
    private static readonly string[] Why = [" de ce ", " why ", " cauz", " cand ", " when ", " unde ", " where "];
    private static readonly string[] Trouble = ["coad", "coz", "queue", "problem"];
    private static readonly string[] Staff = ["operator", " personal", "angajat", " agenti", " staff", " lucrat", " tura ", " ture "];
    private static readonly string[] Events = ["eveniment", "incident", " event"];
    private static readonly string[] Summary = ["rezum", "sumar", "summar", " pe scurt", "overview", " cum a mers", " cum a decurs", " cum a fost simularea",
        " cum a fost rularea", " ce s a intamplat", " bilant", "concluzi"];
    private static readonly string[] WaitStrong = [" cat au asteptat", " cat de mult au asteptat", " cat s a asteptat", " cat a durat asteptarea", " cel mai mult s a asteptat",
        " unde s a asteptat", " unde au asteptat", " cel mai lung timp", " how long did", " longest wait"];
    private static readonly string[] PassengersStrong = [" in total", "neprocesat", " au ramas", " au trecut", " au venit", " au sosit", " cati pasageri au fost",
        " numarul de pasageri", " how many passengers"];
    // Topics without a clear "whole run" cue: present tense asks about the selected moment.
    private static readonly string[] Wait = ["astept", " wait", " p95", " timp"];
    private static readonly string[] Boarding = ["imbarc", "boarding", " risc"];
    private static readonly string[] Load = ["utiliz", "ocup", "capacit", "debit", "throughput", "incarc"];
    private static readonly string[] Queue = ["coad", "coz", "queue"];
    private static readonly string[] Passengers = ["pasager", "passenger", " persoane"];
    private static readonly string[] Present = [" este ", " e ", " sunt ", " exista ", " sta ", " stau ", " is ", " are "];
    private static readonly string[] RunWide = ["simular", "rular", " total", " toata ", " toate ", " intreaga", " in general", " overall", " the run", " whole "];
    private static readonly string[] Help = [" buna ", " salut", " hello ", " hi ", " hey ", " servus ", " ce poti ", " ce stii ", " cum functionezi", " cum te folosesc",
        " ajutor", " help ", " what can you", " ce intrebari", " ce pot intreba", " multumesc", " mersi", " thanks", " thank you"];

    /// <summary>
    /// The whole-run topic of a question already folded to plain lowercase letters, or null when it belongs to the selected zone and minute,
    /// asks about a future or a what-if (those need another run), or is not about the simulation.
    /// </summary>
    public static CopilotRunQuery? Route(string question, IReadOnlyList<CopilotZone> zones)
    {
        var q = Words(question);
        bool Has(string[] words) => words.Any(q.Contains);
        if (Has(Unanswerable) || Has(Moment)) return null;
        if (Has(Compare)) return new("run_compare");
        var zone = Zone(q, zones);
        if (Number(q, @" (?:minut(?:ul)?|minute|min) (\d{1,5})") is { } minute) return new("run_minute", zone, minute);
        if (zone is not null) return new("run_zone", zone);
        if (Has(Advice)) return new("run_advice");
        if (Has(Queue) && Number(q, @"(?:peste|mai mult de|mai multi de|over|more than|above) (\d{1,6})") is { } limit) return new("run_threshold", Value: limit);
        if (Has(Missed)) return new(Has(ByFlight) ? "run_missed_flight" : "run_missed");
        if (Has(Calm)) return new("run_calm");
        if (Has(Peak) || Has(Why) && Has(Trouble)) return new("run_peak");
        if (Has(Staff)) return new("run_staff");
        if (Has(Events)) return new("run_events");
        if (Has(Summary)) return new("run_summary");
        if (Has(WaitStrong)) return new("run_wait");
        if (Has(PassengersStrong)) return new("run_passengers");
        var weak = Has(Wait) ? "run_wait" : Has(Boarding) ? "run_missed" : Has(Load) ? "run_utilization" : Has(Queue) ? "run_peak" : Has(Passengers) ? "run_passengers" : null;
        if (weak is not null) return Has(Present) && !Has(RunWide) ? null : new(weak);
        return Has(Help) ? new("run_help") : null;
    }

    private static string Words(string text) => " " + Regex.Replace(text, @"[^\p{L}\d]+", " ").Trim() + " ";
    private static int? Number(string q, string pattern) => Regex.Match(q, pattern) is { Success: true } m ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    /// <summary>A checkpoint named by its code or name ("cp 2", "checkpoint synthetic 2") or by its number ("controlul 2"); null when none or ambiguous.</summary>
    private static Guid? Zone(string q, IReadOnlyList<CopilotZone> zones)
    {
        static string Fold(string value) => Words(string.Concat((value ?? "").Normalize(System.Text.NormalizationForm.FormD)
            .Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)).ToLowerInvariant());
        // A one-word name such as "Control" would match every question about control; codes are distinctive.
        var named = zones.Where(z => new[] { Fold(z.Code), Fold(z.Name).Count(c => c == ' ') > 2 ? Fold(z.Name) : "" }
            .Any(n => n.Trim().Length > 0 && q.Contains(n))).ToArray();
        if (named.Length == 1) return named[0].Id;
        if (Number(q, @" (?:control(?:ul)?|punct(?:ul)?(?: de control)?|checkpoint(?:ul)?|cp|zona|filtru(?:l)?) ?(\d{1,4}) ") is not { } number) return null;
        string Digits(string value) => Regex.Match(value ?? "", @"(\d+)\D*$").Groups[1].Value;
        var numbered = zones.Where(z => new[] { Digits(z.Code), Digits(z.Name) }.Any(d => d.Length > 0 && int.Parse(d, CultureInfo.InvariantCulture) == number)).ToArray();
        return numbered.Length == 1 ? numbered[0].Id : null;
    }

    /// <summary>"passenger_surge" for an incident surge, the type itself when it can explain a queue, else null.</summary>
    public static string? EventType(string type) => type == IncidentScenarios.PassengerSurge ? "passenger_surge" : EventTypes.Contains(type) ? type : null;

    /// <summary>The longest checkpoint queue in any sample (check-in desks are not Copilot zones), earliest minute on a tie, or null when nobody queued.</summary>
    public static CopilotFocus? Busiest(IEnumerable<KpiSnapshot> samples) => samples.Where(k => k.QueuedPassengers > 0)
        .OrderByDescending(k => k.QueuedPassengers).ThenBy(k => k.Minute)
        .Select(k => new CopilotFocus(k.ZoneId, k.Minute, k.QueuedPassengers)).FirstOrDefault();

    /// <param name="terminals">Each checkpoint's terminal; an operator is only suggested to move within the peak's terminal.</param>
    /// <param name="zone">The checkpoint a run_zone or run_minute question names.</param>
    /// <param name="value">The minute of a run_minute question, or the queue limit of a run_threshold question.</param>
    public static CopilotRunReply Answer(string topic, Guid runId, IReadOnlyList<KpiSnapshot> samples,
        IReadOnlyDictionary<Guid, string> places, IReadOnlyDictionary<Guid, Guid> terminals, IReadOnlyList<CopilotEventFact> events,
        Guid? zone = null, int? value = null)
    {
        if (topic == "run_help")
            return new("answered", "help", "Răspund din datele salvate ale acestei simulări și nu inventez cifre. Mă poți întreba despre cozi, așteptare, "
                + $"pasageri întârziați la îmbarcare, operatori, evenimente, un anumit punct de control sau un anumit minut. De exemplu: {Examples}.", []);
        if (topic == "run_compare")
            return new("insufficient_data", "comparison_not_supported", "Nu pot concluziona: răspund despre o singură simulare și nu amestec cifre din alte rulări. "
                + "Ca să vezi diferențele față de altă rulare, deschide Control Tower → „Compară personalul”.", []);
        // No saved KPI series is missing evidence, not an empty airport.
        if (samples.Count == 0)
            return new("insufficient_data", topic + "_unavailable",
                "Nu pot concluziona: simularea nu are date despre cozi salvate, deci nu pot răspunde pentru toată simularea.", []);
        var facts = new List<CopilotEvidenceFact>();
        int Fact(string id, string label, double value, string unit, Guid? zone, int? minute)
        {
            var rounded = (int)Math.Round(value, MidpointRounding.AwayFromZero);
            facts.Add(new(id, label, rounded, unit, runId, zone, zone is { } z && minute is { } m
                ? $"/runs/{runId}/kpis?zoneId={z}&fromMinute={m}&toMinute={m}" : $"/runs/{runId}/kpis"));
            return rounded;
        }
        string Place(Guid zone) => places.GetValueOrDefault(zone, "punctul de control");
        CopilotRunReply Done(string text) => new("answered", topic, text, facts);
        CopilotRunReply Limited(string text) => new("insufficient_data", topic + "_unavailable", "Nu pot concluziona: " + text, facts);
        // The last sample per checkpoint holds its cumulative totals over the whole run.
        var last = samples.GroupBy(k => k.ZoneId).Select(g => g.MaxBy(k => k.Minute)!).OrderBy(k => Place(k.ZoneId), StringComparer.Ordinal).ToArray();
        static int Staff(KpiSnapshot k) => k.EffectiveCapacity ?? k.Capacity;
        string Label(CopilotEventFact e) => EventNames.GetValueOrDefault(e.Type, e.Type);

        if (topic == "run_wait")
        {
            var waited = last.Where(k => k.WaitSampleCount > 0 && k.MeanWaitMinutes is not null).ToArray();
            if (waited.Length == 0)
                return new("insufficient_data", "run_wait_unavailable",
                    "Nu pot concluziona: niciun pasager nu a terminat încă controlul în această simulare, deci nu pot spune cât s-a așteptat.", []);
            var served = waited.Sum(k => k.WaitSampleCount);
            var mean = waited.Sum(k => k.MeanWaitMinutes!.Value * k.WaitSampleCount) / served;
            var text = $"În medie, un pasager a așteptat {Minutes(Fact("run.meanWait", "Așteptare medie la control, toată simularea (minute întregi)", mean, "minute", null, null))} la control. "
                + $"Au trecut prin control {People(Fact("run.served", "Pasageri care au terminat controlul", served, "pasageri", null, null))}.";
            var worst = waited.MaxBy(k => k.MeanWaitMinutes!.Value)!;
            var name = Place(worst.ZoneId);
            if (waited.Length > 1)
                text += $" Cel mai mult s-a așteptat la {name}: în medie {Minutes(Fact("worst.meanWait", $"{name} · așteptare medie (minute întregi)", worst.MeanWaitMinutes!.Value, "minute", worst.ZoneId, worst.Minute))}.";
            if (worst.P95WaitMinutes is { } p95)
            {
                var most = Fact("worst.p95Wait", $"{name} · P95 așteptare (minute întregi)", p95, "minute", worst.ZoneId, worst.Minute);
                text += $" La {name}, aproape toți pasagerii au așteptat {(most < 1 ? "sub un minut" : "cel mult " + Minutes(most))}.";
            }
            return Done(text);
        }

        if (topic == "run_zone" && zone is { } chosen)
        {
            var series = samples.Where(k => k.ZoneId == chosen).ToArray();
            var name = Place(chosen);
            if (series.Length == 0) return Limited($"simularea nu a salvat date pentru {name}.");
            var top = Busiest(series);
            var end = series.MaxBy(k => k.Minute)!;
            string text;
            if (top is null) text = $"La {name} nu s-a format coadă în această simulare; au lucrat acolo {Operators(Fact("zone.staff", $"{name} · operatori la final", Staff(end), "operatori", chosen, end.Minute))}.";
            else
            {
                var then = series.First(k => k.Minute == top.Minute);
                text = $"La {name}, cea mai mare coadă a fost de {People(Fact("zone.queue", $"{name} · cea mai mare coadă", top.QueuedPassengers, "pasageri", chosen, top.Minute))}, "
                    + $"la minutul {Fact("zone.minute", $"{name} · minutul celei mai mari cozi", top.Minute, "minut", chosen, top.Minute)}, "
                    + $"cu {Operators(Fact("zone.staff", $"{name} · operatori la acel minut", Staff(then), "operatori", chosen, top.Minute))} la lucru; "
                    + "coada a crescut pentru că soseau mai mulți pasageri decât puteau fi procesați.";
                var before = events.Where(e => (e.CheckpointId is null || e.CheckpointId == chosen) && e.Minute >= 0 && e.Minute <= top.Minute).OrderBy(e => e.Minute).ToArray();
                if (before.Length > 0)
                    text += $" Înainte de vârf: {Label(before[^1])} la minutul {Fact("zone.event", $"{name} · ultimul eveniment înainte de vârf", before[^1].Minute, "minut", chosen, top.Minute)}"
                        + (before.Length > 1 ? $" ({Count(Fact("zone.events", $"{name} · evenimente până la vârf", before.Length, "evenimente", chosen, top.Minute), "eveniment", "evenimente")} în total)." : ".");
            }
            if (end.WaitSampleCount > 0 && end.MeanWaitMinutes is { } mean)
                text += $" Au trecut prin control {People(Fact("zone.served", $"{name} · pasageri care au terminat controlul", end.WaitSampleCount, "pasageri", chosen, end.Minute))}, "
                    + $"cu o așteptare medie de {Minutes(Fact("zone.meanWait", $"{name} · așteptare medie (minute întregi)", mean, "minute", chosen, end.Minute))}.";
            if (end.BoardingAtRiskCount > 0)
                text += $" {Count(Fact("zone.late", $"{name} · pasageri întârziați la îmbarcare", end.BoardingAtRiskCount, "pasageri", chosen, end.Minute), "pasager de aici a", "pasageri de aici au")} întârziat la îmbarcare.";
            return Done(text);
        }

        if (topic == "run_minute" && value is { } asked)
        {
            var finish = samples.Max(k => k.Minute);
            var minuteFact = Fact("asked.minute", "Minutul din întrebare", asked, "minut", null, null);
            if (asked > finish)
                return Limited($"simularea are date doar până la minutul {Fact("run.end", "Ultimul minut al simulării", finish, "minut", null, null)}, nu și la minutul {minuteFact}.");
            var rows = samples.Where(k => k.Minute <= asked && (zone is null || k.ZoneId == zone)).GroupBy(k => k.ZoneId)
                .Select(g => g.MaxBy(k => k.Minute)!).OrderBy(k => Place(k.ZoneId), StringComparer.Ordinal).ToArray();
            if (rows.Length == 0) return Limited($"simularea nu a salvat date până la minutul {minuteFact}.");
            var parts = rows.Select((k, i) => $"{Place(k.ZoneId)} {People(Fact($"minute.{i}.queue", $"{Place(k.ZoneId)} · pasageri la coadă", k.QueuedPassengers, "pasageri", k.ZoneId, k.Minute))} la coadă, "
                + Operators(Fact($"minute.{i}.staff", $"{Place(k.ZoneId)} · operatori", Staff(k), "operatori", k.ZoneId, k.Minute)));
            var recorded = rows.Max(k => k.Minute);
            var text = $"La minutul {minuteFact}"
                + (recorded != asked ? $" (ultima înregistrare: minutul {Fact("minute.recorded", "Minutul înregistrării folosite", recorded, "minut", null, null)})" : "")
                + $": {string.Join("; ", parts)}. Până atunci trecuseră prin control {People(Fact("minute.processed", "Pasageri care au terminat controlul până atunci", rows.Sum(k => k.ProcessedPassengers), "pasageri", null, null))}.";
            var recent = events.Where(e => (zone is null || e.CheckpointId is null || e.CheckpointId == zone) && e.Minute >= 0 && e.Minute <= asked).MaxBy(e => e.Minute);
            if (recent is not null)
                text += $" Ultimul eveniment până atunci: {Label(recent)} la minutul {Fact("minute.event", "Minutul ultimului eveniment", recent.Minute, "minut", recent.CheckpointId, null)}"
                    + (recent.CheckpointId is { } where ? $", la {Place(where)}." : ".");
            return Done(text);
        }

        if (topic is "run_missed" or "run_missed_flight")
        {
            var population = last.Sum(k => k.BoardingRiskPopulationCount);
            if (population == 0) return Limited("simularea nu are pasageri cu oră de îmbarcare, deci nu pot spune cine a întârziat.");
            var late = last.Sum(k => k.BoardingAtRiskCount);
            var text = topic == "run_missed_flight" ? "Simularea păstrează întârzierile pe puncte de control, nu pe zboruri, deci nu pot spune care zbor a avut cei mai mulți. " : "";
            var all = People(Fact("run.boardingPopulation", "Pasageri cu oră de îmbarcare", population, "pasageri", null, null));
            if (late == 0) text += $"Niciun pasager nu a întârziat la îmbarcare, din {all} cu zbor în simulare.";
            else
            {
                text += $"{Count(Fact("run.late", "Pasageri întârziați la îmbarcare", late, "pasageri", null, null), "pasager a", "pasageri au")} întârziat la îmbarcare, din {all} cu zbor în simulare";
                var gate = last.Sum(k => k.LateAtGateCount); var unfinished = last.Sum(k => k.MissedDeadlineUnfinishedCount);
                var reasons = new List<string>();
                if (gate > 0) reasons.Add($"{Count(Fact("run.lateAtGate", "Ajunși la poartă după închiderea îmbarcării", gate, "pasageri", null, null), "pasager a ajuns", "pasageri au ajuns")} la poartă după închiderea îmbarcării");
                if (unfinished > 0) reasons.Add($"{Count(Fact("run.unfinished", "Nu terminaseră controlul la închiderea îmbarcării", unfinished, "pasageri", null, null), "pasager nu terminase", "pasageri nu terminaseră")} controlul la timp");
                text += reasons.Count > 0 ? ": " + string.Join(", iar ", reasons) + "." : ".";
                var byZone = last.Where(k => k.BoardingAtRiskCount > 0).ToArray();
                if (byZone.Length > 1)
                    text += " Pe puncte de control: " + string.Join(", ", byZone.Select((k, i) =>
                        $"{Place(k.ZoneId)} {Fact($"zone.{i}.late", $"{Place(k.ZoneId)} · pasageri întârziați la îmbarcare", k.BoardingAtRiskCount, "pasageri", k.ZoneId, k.Minute)}")) + ".";
            }
            return topic == "run_missed_flight" ? new("insufficient_data", "run_missed_flight_unavailable", text, facts) : Done(text);
        }

        if (topic is "run_passengers" or "run_summary")
        {
            var population = last.Sum(k => k.PopulationCount); var processed = last.Sum(k => k.ProcessedPassengers);
            var done = Fact("run.processed", "Pasageri care au terminat controlul", processed, "pasageri", null, null);
            var text = (topic == "run_summary" ? "Pe scurt: în" : "În") + $" simulare au fost {People(Fact("run.population", "Pasageri în simulare", population, "pasageri", null, null))}, "
                + (population > processed ? $"iar {People(done)} au trecut prin control; la final {Count(Fact("run.unprocessed", "Pasageri care nu terminaseră controlul la final", population - processed, "pasageri", null, null), "pasager nu terminase", "pasageri nu terminaseră")} controlul (în coadă, la control sau încă nesosiți)."
                    : "și toți au trecut prin control.");
            if (topic == "run_passengers")
            {
                if (last.Length > 1)
                    text += " Pe puncte de control: " + string.Join(", ", last.Select((k, i) =>
                        $"{Place(k.ZoneId)} {Fact($"zone.{i}.processed", $"{Place(k.ZoneId)} · pasageri care au terminat controlul", k.ProcessedPassengers, "pasageri", k.ZoneId, k.Minute)}")) + ".";
                return Done(text);
            }
            var waited = last.Where(k => k.WaitSampleCount > 0 && k.MeanWaitMinutes is not null).ToArray();
            if (waited.Length > 0)
                text += $" În medie, un pasager a așteptat {Minutes(Fact("run.meanWait", "Așteptare medie la control, toată simularea (minute întregi)", waited.Sum(k => k.MeanWaitMinutes!.Value * k.WaitSampleCount) / waited.Sum(k => k.WaitSampleCount), "minute", null, null))} la control.";
            var top = Busiest(samples);
            text += top is null ? " Nu s-a format coadă la niciun punct de control."
                : $" Cea mai mare coadă: {People(Fact("peak.queue", $"{Place(top.ZoneId)} · pasageri la coadă", top.QueuedPassengers, "pasageri", top.ZoneId, top.Minute))} la {Place(top.ZoneId)}, "
                    + $"la minutul {Fact("peak.minute", $"{Place(top.ZoneId)} · minutul celei mai mari cozi", top.Minute, "minut", top.ZoneId, top.Minute)}.";
            var late = last.Sum(k => k.BoardingAtRiskCount);
            if (last.Sum(k => k.BoardingRiskPopulationCount) > 0)
                text += late == 0 ? " Niciun pasager nu a întârziat la îmbarcare."
                    : $" {Count(Fact("run.late", "Pasageri întârziați la îmbarcare", late, "pasageri", null, null), "pasager a", "pasageri au")} întârziat la îmbarcare.";
            var happened = events.Count(e => e.Minute >= 0);
            if (happened > 0)
                text += $" Scenariul a avut {Count(Fact("run.events", "Evenimente din scenariu", happened, "evenimente", null, null), "eveniment", "evenimente")} (întreabă „Ce evenimente au avut loc?”).";
            return Done(text);
        }

        if (topic == "run_staff")
        {
            var rows = samples.GroupBy(k => k.ZoneId).Select(g => (Zone: g.Key, Min: g.Min(Staff), Max: g.Max(Staff), End: g.MaxBy(k => k.Minute)!.Minute))
                .OrderBy(r => Place(r.Zone), StringComparer.Ordinal).ToArray();
            var together = samples.GroupBy(k => k.Minute).Max(g => g.Sum(Staff));
            var parts = rows.Select((r, i) => r.Min == r.Max
                ? $"{Place(r.Zone)} {Operators(Fact($"zone.{i}.staff", $"{Place(r.Zone)} · operatori", r.Max, "operatori", r.Zone, r.End))}"
                : $"{Place(r.Zone)} între {Fact($"zone.{i}.staffMin", $"{Place(r.Zone)} · cei mai puțini operatori", r.Min, "operatori", r.Zone, null)} și "
                    + Operators(Fact($"zone.{i}.staffMax", $"{Place(r.Zone)} · cei mai mulți operatori", r.Max, "operatori", r.Zone, null)));
            return Done($"La control au lucrat cel mult {Operators(Fact("run.staff", "Operatori la control în același timp (cel mult)", together, "operatori", null, null))} în același timp. "
                + $"Pe puncte de control: {string.Join(", ", parts)}.");
        }

        if (topic == "run_events")
        {
            var list = events.Where(e => e.Minute >= 0).OrderBy(e => e.Minute).ThenBy(e => e.Id).ToArray();
            Fact("run.events", "Evenimente din scenariu", list.Length, "evenimente", null, null);
            if (list.Length == 0) return Done("Scenariul nu a avut evenimente care să afecteze controlul, cum ar fi valuri de pasageri, controale închise sau scăderi de personal.");
            var shown = list.Take(10).Select((e, i) => $"{Label(e)} la minutul {Fact($"event.{i}.minute", $"{Label(e)} · minutul", e.Minute, "minut", e.CheckpointId, null)}"
                + (e.CheckpointId is { } where ? $" ({Place(where)})" : ""));
            return Done($"Scenariul a avut {Count(list.Length, "eveniment", "evenimente")}: {string.Join("; ", shown)}" + (list.Length > 10 ? "; și altele." : "."));
        }

        if (topic == "run_utilization")
        {
            var used = last.Where(k => k.CumulativeUtilization is not null).ToArray();
            if (used.Length == 0) return Limited("simularea nu a salvat cât de ocupați au fost operatorii.");
            var parts = used.Select((k, i) => $"{Place(k.ZoneId)} {Fact($"zone.{i}.utilization", $"{Place(k.ZoneId)} · ocupare operatori, toată simularea", k.CumulativeUtilization!.Value * 100, "%", k.ZoneId, k.Minute)}% "
                + $"({Fact($"zone.{i}.processed", $"{Place(k.ZoneId)} · pasageri procesați", k.ProcessedPassengers, "pasageri", k.ZoneId, k.Minute)} procesați)");
            return Done($"Cât din timp au fost ocupați operatorii, pe toată simularea: {string.Join(", ", parts)}. "
                + "Un procent mic înseamnă operatori adesea liberi; aproape de sută la sută înseamnă că nu mai făceau față.");
        }

        var peak = Busiest(samples);
        var maxima = samples.GroupBy(k => k.ZoneId).Select(g => (Zone: g.Key, Max: g.Max(k => k.QueuedPassengers))).OrderBy(r => Place(r.Zone), StringComparer.Ordinal).ToArray();

        if (topic == "run_calm")
        {
            var calm = maxima.OrderBy(r => r.Max).First();
            var name = Place(calm.Zone);
            var staff = Operators(Fact("calm.staff", $"{name} · cei mai mulți operatori", samples.Where(k => k.ZoneId == calm.Zone).Max(Staff), "operatori", calm.Zone, null));
            return Done(calm.Max == 0 ? $"Cel mai liber a fost {name}: acolo nu s-a format coadă deloc, cu cel mult {staff} la lucru."
                : $"Cel mai liber a fost {name}: coada nu a depășit {People(Fact("calm.queue", $"{name} · cea mai mare coadă", calm.Max, "pasageri", calm.Zone, null))}, cu cel mult {staff} la lucru.");
        }

        if (topic == "run_threshold" && value is { } limit)
        {
            var bound = People(Fact("asked.limit", "Pragul din întrebare", limit, "pasageri", null, null));
            var over = maxima.Where(r => r.Max > limit).ToArray();
            if (over.Length == 0)
                return Done($"Coada nu a depășit {bound} la niciun punct de control." + (peak is null ? "" : $" Cea mai mare a fost de {People(Fact("peak.queue", $"{Place(peak.ZoneId)} · pasageri la coadă", peak.QueuedPassengers, "pasageri", peak.ZoneId, peak.Minute))}, la {Place(peak.ZoneId)}."));
            var parts = over.Select((r, i) =>
            {
                var first = samples.Where(k => k.ZoneId == r.Zone && k.QueuedPassengers > limit).Min(k => k.Minute);
                return $"{Place(r.Zone)} (prima dată la minutul {Fact($"zone.{i}.first", $"{Place(r.Zone)} · primul minut peste prag", first, "minut", r.Zone, first)}, "
                    + $"cel mult {People(Fact($"zone.{i}.max", $"{Place(r.Zone)} · cea mai mare coadă", r.Max, "pasageri", r.Zone, null))})";
            });
            return Done($"Coada a depășit {bound} la {Count(Fact("threshold.zones", "Puncte de control peste prag", over.Length, "puncte de control", null, null), "punct de control", "puncte de control")}: {string.Join("; ", parts)}.");
        }

        if (peak is null)
        {
            Fact("run.maxQueue", "Cea mai mare coadă din simulare", 0, "pasageri", null, null);
            return Done(topic == "run_advice"
                ? "Nu s-a format coadă la niciun punct de control, deci nu văd nimic de corectat la personal în această simulare."
                : "În această simulare nu s-a format coadă la niciun punct de control.");
        }
        var at = samples.First(k => k.ZoneId == peak.ZoneId && k.Minute == peak.Minute);
        var place = Place(peak.ZoneId);
        var queued = People(Fact("peak.queue", $"{place} · pasageri la coadă", peak.QueuedPassengers, "pasageri", peak.ZoneId, peak.Minute));
        var minute = Fact("peak.minute", $"{place} · minutul celei mai mari cozi", peak.Minute, "minut", peak.ZoneId, peak.Minute);
        var operators = Operators(Fact("peak.staff", $"{place} · operatori la acel minut", Staff(at), "operatori", peak.ZoneId, peak.Minute));
        if (topic == "run_advice")
        {
            // Somewhere calm with an operator to spare, so moving one leaves the place staffed.
            var calm = samples.Where(k => k.ZoneId != peak.ZoneId && terminals.TryGetValue(k.ZoneId, out var t)
                    && terminals.TryGetValue(peak.ZoneId, out var home) && t == home).GroupBy(k => k.ZoneId)
                .Select(g => (Zone: g.Key, Max: g.Max(k => k.QueuedPassengers), Staff: g.FirstOrDefault(k => k.Minute == peak.Minute) is { } s ? Staff(s) : 0))
                .Where(c => c.Staff > 1).OrderBy(c => c.Max).ThenBy(c => Place(c.Zone), StringComparer.Ordinal).Select(c => (Guid?)c.Zone).FirstOrDefault();
            return Done($"Cea mai mare presiune a fost la {place}: {queued} la coadă la minutul {minute}, cu {operators} la lucru. "
                + $"Primul lucru de încercat: încă un operator la {place}, cu puțin înainte de acel moment"
                + (calm is { } c ? $", de exemplu mutat de la {Place(c)}, unde coada a fost cea mai mică. " : ". ")
                + "Nu pot garanta efectul din această singură simulare: în Control Tower, „Optimizare propusă de AI” simulează variantele și le compară.");
        }
        var answer = $"Cea mai mare coadă a fost la {place}: {queued} la minutul {minute}. În acel moment lucrau acolo {operators}";
        if (at.ThroughputPerMinute is { } rate)
            answer += $" și se procesau aproximativ {People(Fact("peak.throughput", $"{place} · pasageri procesați pe minut", rate, "pasageri/minut", peak.ZoneId, peak.Minute))} pe minut";
        answer += "; coada a crescut pentru că soseau mai mulți pasageri decât puteau fi procesați.";
        var touching = events.Count(e => (e.CheckpointId is null || e.CheckpointId == peak.ZoneId) && e.Minute >= 0 && e.Minute <= peak.Minute);
        if (touching > 0)
            answer += $" Până atunci, {Count(Fact("peak.events", $"{place} · evenimente din scenariu până la vârf", touching, "evenimente", peak.ZoneId, peak.Minute), "eveniment din scenariu a afectat", "evenimente din scenariu au afectat")} acest punct (vezi „Evenimente în zonă”).";
        var second = samples.Where(k => k.ZoneId != peak.ZoneId).GroupBy(k => k.ZoneId)
            .Select(g => Busiest(g)).OfType<CopilotFocus>().OrderByDescending(f => f.QueuedPassengers).ThenBy(f => f.Minute).FirstOrDefault();
        if (second is not null)
        {
            var other = Place(second.ZoneId);
            answer += $" Pe locul doi: {other}, cu {People(Fact("second.queue", $"{other} · cea mai mare coadă", second.QueuedPassengers, "pasageri", second.ZoneId, second.Minute))} la minutul {Fact("second.minute", $"{other} · minutul celei mai mari cozi", second.Minute, "minut", second.ZoneId, second.Minute)}.";
        }
        return Done(answer);
    }

    private static string People(int n) => Count(n, "pasager", "pasageri");
    private static string Operators(int n) => Count(n, "operator", "operatori");
    private static string Minutes(int n) => n < 1 ? "sub un minut" : Count(n, "minut", "minute");
    /// <summary>Romanian counting: "1 pasager", "5 pasageri", "20 de pasageri".</summary>
    public static string Count(int n, string one, string many) => n == 1 ? $"1 {one}"
        : n != 0 && n % 100 is 0 or >= 20 ? $"{n.ToString(CultureInfo.InvariantCulture)} de {many}" : $"{n.ToString(CultureInfo.InvariantCulture)} {many}";
}
