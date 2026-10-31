using System.Globalization;
using System.Text;
using AeroSynapse.Domain.DigitalTwin;

namespace AeroSynapse.Application.Storage;

public static class FlightScheduleCsv
{
    public const int MaxCharacters = 1_000_000;
    public const int MaxFlights = 1000;
    public const string Header = "id,flightNumber,gateCode,departureAtUtc,boardingOpensAtUtc,boardingClosesAtUtc,passengerCapacity,delayMinutes,status,isSynthetic";

    public static string Export(FlightSchedule schedule)
    {
        var gates = schedule.Gates.ToDictionary(gate => gate.Id);
        var csv = new StringBuilder(Header + "\r\n");
        foreach (var flight in schedule.Flights)
        {
            string[] cells = [flight.Id.ToString(), flight.Code, gates[flight.GateId].Code,
                flight.DepartureAtUtc.ToString("O", CultureInfo.InvariantCulture),
                flight.BoardingOpensAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture),
                flight.BoardingClosesAtUtc!.Value.ToString("O", CultureInfo.InvariantCulture),
                flight.PassengerCapacity.ToString(CultureInfo.InvariantCulture), flight.DelayMinutes.ToString(CultureInfo.InvariantCulture),
                flight.Status, "true"];
            csv.AppendJoin(',', cells.Select(Quote)).Append("\r\n");
        }
        return csv.ToString();
    }

    public static IReadOnlyList<ScenarioFlight> Import(string? csv, IReadOnlyList<ScheduleGate> gates)
    {
        if (string.IsNullOrWhiteSpace(csv)) throw Invalid("csv", "A synthetic CSV schedule is required.");
        if (csv.Length > MaxCharacters) throw Invalid("csv", $"CSV must contain at most {MaxCharacters} characters.");
        var rows = Parse(csv.TrimStart('\uFEFF'));
        if (rows.Count == 0 || !rows[0].SequenceEqual(Header.Split(',')))
            throw Invalid("csv.header", $"Expected header: {Header}");
        if (rows.Count - 1 > MaxFlights) throw Invalid("csv", $"Import supports at most {MaxFlights} flights.");
        var flights = new List<ScenarioFlight>();
        var errors = new Dictionary<string, string[]>();
        for (var index = 1; index < rows.Count; index++)
        {
            var row = rows[index];
            var field = $"csv.rows[{index + 1}]";
            void Error(string cell, string message) => errors[field + "." + cell] = [message];
            if (row.Length != 10) { Error("columns", "Expected 10 columns."); continue; }
            var id = Guid.NewGuid();
            if (row[0].Length > 0 && (!Guid.TryParse(row[0], out id) || id == Guid.Empty)) Error("id", "Use a nonempty GUID or leave blank for a new flight.");
            var gateCode = UnquoteSpreadsheetText(row[2]);
            var matches = gates.Where(gate => string.Equals(gate.Code, gateCode, StringComparison.OrdinalIgnoreCase)).ToArray();
            var gate = matches.SingleOrDefault(gate => gate.Code == gateCode) ?? (matches.Length == 1 ? matches[0] : null);
            if (gate is null) Error("gateCode", "Gate code is absent or ambiguous in the scenario's published layout.");
            var departure = Time(row[3], "departureAtUtc");
            var open = Time(row[4], "boardingOpensAtUtc");
            var close = Time(row[5], "boardingClosesAtUtc");
            var capacity = Integer(row[6], "passengerCapacity");
            var delay = Integer(row[7], "delayMinutes");
            if (row[9] != "true") Error("isSynthetic", "Every imported flight must explicitly be marked demo with true.");
            flights.Add(new(id, row[1], gate?.Id ?? Guid.Empty, departure, capacity, open, close, delay, row[8], true));

            DateTime Time(string text, string cell)
            {
                if (!DateTime.TryParseExact(text, ["yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"],
                    CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
                    Error(cell, "Use an ISO 8601 UTC timestamp with the Z suffix.");
                return time;
            }
            int Integer(string text, string cell)
            {
                if (!int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value))
                    Error(cell, "Use a 32-bit integer.");
                return value;
            }
        }
        if (errors.Count > 0) throw new ScenarioValidationException(errors);
        return flights;
    }

    private static string Quote(string value)
    {
        // Gate names may come from a user-defined layout. Preserve text when opened in a spreadsheet.
        if (value.StartsWith('\'') || value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@') value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
    private static string UnquoteSpreadsheetText(string value) => value.StartsWith('\'') &&
        (value[1..].StartsWith('\'') || value[1..].TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@') ? value[1..] : value;

    private static IReadOnlyList<string[]> Parse(string csv)
    {
        var rows = new List<string[]>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var closed = false;
        for (var index = 0; index < csv.Length; index++)
        {
            var c = csv[index];
            if (quoted)
            {
                if (c == '"')
                {
                    if (index + 1 < csv.Length && csv[index + 1] == '"') { cell.Append('"'); index++; }
                    else { quoted = false; closed = true; }
                }
                else cell.Append(c);
                continue;
            }
            if (c == '"')
            {
                if (cell.Length > 0 || closed) throw Invalid("csv", "Unexpected quote in an unquoted field.");
                quoted = true;
            }
            else if (c is ',' or '\r' or '\n')
            {
                row.Add(cell.ToString()); cell.Clear(); closed = false;
                if (c != ',')
                {
                    if (c == '\r' && index + 1 < csv.Length && csv[index + 1] == '\n') index++;
                    rows.Add(row.ToArray()); row.Clear();
                    if (rows.Count > MaxFlights + 1) throw Invalid("csv", $"Import supports at most {MaxFlights} flights.");
                }
            }
            else
            {
                if (closed) throw Invalid("csv", "Unexpected content after a quoted field.");
                cell.Append(c);
            }
        }
        if (quoted) throw Invalid("csv", "Unterminated quoted field.");
        if (cell.Length > 0 || row.Count > 0 || closed) { row.Add(cell.ToString()); rows.Add(row.ToArray()); }
        return rows;
    }

    private static ScenarioValidationException Invalid(string field, string message) => new(new Dictionary<string, string[]> { [field] = [message] });
}
