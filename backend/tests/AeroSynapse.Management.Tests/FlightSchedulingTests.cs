using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class FlightSchedulingTests
{
    private static readonly DateTime Start = new(2026, 10, 13, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid SceneId = Guid.NewGuid();
    private static readonly Gate[] Gates = Enumerable.Range(1, 5).Select(number => new Gate
        { Id = Guid.NewGuid(), Code = $"G-{number}", Name = $"Poartă demo {number}", Capacity = 150, IsSynthetic = true }).ToArray();
    private static SceneDetails Layout => new(new(SceneId, "demo", "Demo", "", true), [], [], Gates, [], []);
    private static ScenarioFlight Flight(int index = 0) => new(Guid.NewGuid(), $"DEMO-{index + 1}", Gates[index].Id,
        Start.AddMinutes(40 + index * 10), 120, Start.AddMinutes(10 + index * 10), Start.AddMinutes(35 + index * 10));
    private static ScenarioWriteRequest Request(params ScenarioFlight[] flights) => new("demo", "Demo", "", SceneId, Guid.NewGuid(),
        new ScenarioInputs { StartsAtUtc = Start, DurationMinutes = 90, Flights = flights });
    private static FlightSchedule Schedule(params ScenarioFlight[] flights) => new(Guid.NewGuid(), 1, Start, 90, flights,
        Gates.Select(gate => new ScheduleGate(gate.Id, gate.Code, gate.Name, gate.Capacity)).ToArray());

    [Fact]
    public void Five_flights_fit_in_ninety_simulated_minutes_and_are_demo()
    {
        var flights = Enumerable.Range(0, 5).Select(Flight).ToArray();
        ScenarioValidator.Validate(Request(flights), Layout);
        Assert.All(flights, flight => Assert.True(flight.IsSynthetic));
        Assert.Empty(FlightScheduling.Conflicts(Request(flights).Inputs));
    }

    [Theory]
    [InlineData("close-at-departure", "boardingClosesAtUtc")]
    [InlineData("close-after-departure", "boardingClosesAtUtc")]
    [InlineData("open-at-close", "boardingClosesAtUtc")]
    [InlineData("missing-open", "boardingOpensAtUtc")]
    [InlineData("missing-close", "boardingClosesAtUtc")]
    [InlineData("local-open", "boardingOpensAtUtc")]
    [InlineData("negative-delay", "delayMinutes")]
    [InlineData("outside-horizon", "departureAtUtc")]
    [InlineData("invalid-status", "status")]
    [InlineData("real-flight", "isSynthetic")]
    [InlineData("zero-capacity", "passengerCapacity")]
    [InlineData("unknown-gate", "gateId")]
    [InlineData("formula-number", "code")]
    public void Invalid_flight_input_has_a_field_error(string invalid, string field)
    {
        var flight = Flight();
        flight = invalid switch
        {
            "close-at-departure" => flight with { BoardingClosesAtUtc = flight.DepartureAtUtc },
            "close-after-departure" => flight with { BoardingClosesAtUtc = flight.DepartureAtUtc.AddMinutes(1) },
            "open-at-close" => flight with { BoardingOpensAtUtc = flight.BoardingClosesAtUtc },
            "missing-open" => flight with { BoardingOpensAtUtc = null },
            "missing-close" => flight with { BoardingClosesAtUtc = null },
            "local-open" => flight with { BoardingOpensAtUtc = DateTime.SpecifyKind(flight.BoardingOpensAtUtc!.Value, DateTimeKind.Unspecified) },
            "negative-delay" => flight with { DelayMinutes = -1 },
            "outside-horizon" => flight with { DelayMinutes = 51 },
            "invalid-status" => flight with { Status = "real-time" },
            "real-flight" => flight with { IsSynthetic = false },
            "zero-capacity" => flight with { PassengerCapacity = 0 },
            "unknown-gate" => flight with { GateId = Guid.NewGuid() },
            "formula-number" => flight with { Code = "=1+1" },
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        var exception = Assert.Throws<ScenarioValidationException>(() => ScenarioValidator.Validate(Request(flight), Layout));
        Assert.Contains($"inputs.flights[0].{field}", exception.Errors.Keys);
    }

    [Fact]
    public void Overlap_reports_both_flights_and_the_gate_including_after_boarding_closes()
    {
        var first = Flight();
        var second = Flight(1) with { GateId = first.GateId, BoardingOpensAtUtc = Start.AddMinutes(36) };
        var request = Request(first, second);
        var conflict = Assert.Single(FlightScheduling.Conflicts(request.Inputs));
        Assert.Equal(Start.AddMinutes(36), conflict.StartsAtUtc);
        Assert.Equal(first.DepartureAtUtc, conflict.EndsAtUtc);
        var exception = Assert.Throws<ScenarioValidationException>(() => ScenarioValidator.Validate(request, Layout));
        var message = Assert.Single(exception.Errors["inputs.flights"]);
        Assert.Contains(first.Code, message); Assert.Contains(second.Code, message); Assert.Contains(Gates[0].Code, message);
    }

    [Fact]
    public void Back_to_back_gate_use_is_allowed_but_delay_can_create_a_conflict_and_cancellation_releases_it()
    {
        var first = Flight();
        var second = Flight(1) with { GateId = first.GateId, BoardingOpensAtUtc = first.DepartureAtUtc };
        ScenarioValidator.Validate(Request(first, second), Layout);
        Assert.Single(FlightScheduling.Conflicts(Request(first with { DelayMinutes = 1 }, second).Inputs));
        ScenarioValidator.Validate(Request(first with { DelayMinutes = 1, Status = "cancelled" }, second), Layout);
    }

    [Fact]
    public void Legacy_boarding_defaults_work_for_short_scenarios_and_timestamps_do_not_overflow()
    {
        var flight = new ScenarioFlight(Guid.NewGuid(), "DEMO-1", Gates[0].Id, Start.AddSeconds(60), 120);
        var inputs = Request(flight) with { Inputs = Request(flight).Inputs with { DurationMinutes = 1 } };
        ScenarioValidator.Validate(inputs, Layout);
        var resolved = FlightScheduling.Resolve(flight, Start);
        Assert.Equal(Start, resolved.BoardingOpensAtUtc); Assert.Equal(Start.AddSeconds(30), resolved.BoardingClosesAtUtc);
        Assert.False(FlightScheduling.TryDelayed(DateTime.SpecifyKind(DateTime.MaxValue, DateTimeKind.Utc), 1, out _));
    }

    [Fact]
    public void Passenger_flows_must_finish_by_effective_boarding_close()
    {
        var flight = Flight();
        var population = new PassengerPopulation(Guid.NewGuid(), "Demo", 120);
        var route = new PassengerRoute { Id = Guid.NewGuid(), ToGateId = flight.GateId };
        var inputs = Request(flight).Inputs with
        {
            Populations = [population],
            Flows = [new(Guid.NewGuid(), population.Id, flight.Id, route.Id, 120, Start, Start.AddMinutes(36))]
        };
        Assert.Throws<ScenarioValidationException>(() => ScenarioValidator.Validate(Request(flight) with { Inputs = inputs }, Layout with { Routes = [route] }));
        ScenarioValidator.Validate(Request(flight) with { Inputs = inputs with { Flights = [flight with { DelayMinutes = 1 }] } }, Layout with { Routes = [route] });
    }

    [Fact]
    public void Csv_round_trip_preserves_ids_times_delay_status_and_demo_marker_with_bom_and_quoted_gate_codes()
    {
        var flights = Enumerable.Range(0, 5).Select(index => Flight(index) with { DelayMinutes = 2, Status = "delayed" }).ToArray();
        var schedule = Schedule(flights);
        schedule = schedule with { Gates = schedule.Gates.Select((gate, index) => index == 0 ? gate with { Code = "G,\"A\"\nDEMO" } : gate).ToArray() };
        var csv = FlightScheduleCsv.Export(schedule);
        var imported = FlightScheduleCsv.Import("\uFEFF" + csv, schedule.Gates);
        Assert.Equal(flights, imported);
        Assert.Contains("true", csv);
        var newIdCsv = csv.Replace($"\"{flights[0].Id}\"", "\"\"");
        Assert.NotEqual(Guid.Empty, FlightScheduleCsv.Import(newIdCsv, schedule.Gates)[0].Id);
    }

    [Theory]
    [InlineData("bad-header")]
    [InlineData("bad-quotes")]
    [InlineData("trailing-junk")]
    [InlineData("real-data")]
    [InlineData("local-time")]
    [InlineData("decimal-capacity")]
    [InlineData("bad-id")]
    [InlineData("unknown-gate")]
    [InlineData("too-large")]
    public void Malformed_or_non_demo_csv_is_rejected(string invalid)
    {
        var flight = Flight();
        var schedule = Schedule(flight);
        var csv = FlightScheduleCsv.Export(schedule);
        csv = invalid switch
        {
            "bad-header" => csv.Replace("flightNumber", "number"),
            "bad-quotes" => csv + "\"unfinished",
            "trailing-junk" => csv.Replace("\"DEMO-1\"", "\"DEMO-1\"junk"),
            "real-data" => csv.Replace("\"true\"", "\"false\""),
            "local-time" => csv.Replace(".0000000Z", ".0000000+03:00"),
            "decimal-capacity" => csv.Replace("\"120\"", "\"120.5\""),
            "bad-id" => csv.Replace(flight.Id.ToString(), "invalid"),
            "unknown-gate" => csv.Replace("G-1", "G-REAL"),
            "too-large" => new string('x', FlightScheduleCsv.MaxCharacters + 1),
            _ => throw new ArgumentOutOfRangeException(nameof(invalid))
        };
        Assert.Throws<ScenarioValidationException>(() => FlightScheduleCsv.Import(csv, schedule.Gates));
    }
}
