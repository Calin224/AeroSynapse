using System.Text.Json;
using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Contract.Tests;

// Shift plans: the Python plan is re-checked here from its shifts alone, and scenario shifts become engine steps.
public sealed class ShiftPlanTests
{
    private static readonly string Root = Path.Combine(AppContext.BaseDirectory, "contracts", "staffing", "v1", "examples");
    private static JsonElement Example(string name) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(Root, name + ".json")));
    private static ShiftPlanResult Result() => ContractJson.Read<ShiftPlanResult>(Example("shift-plan-response"), "ShiftPlanResult");
    private static readonly Guid Terminal = Guid.NewGuid(), ControlA = Guid.NewGuid(), ControlB = Guid.NewGuid(), Person = Guid.NewGuid();

    [Fact]
    public void Python_plan_roundtrips_and_reconciles_shift_by_shift()
    {
        Assert.Empty(VersionedContract.Validate("ShiftPlanResult", Example("shift-plan-response")));
        var result = Result();
        Assert.True(JsonElement.DeepEquals(Example("shift-plan-response"), JsonSerializer.SerializeToElement(result, ContractJson.Options)));
        Assert.True(ShiftPlanValidation.Valid(result));
        Assert.Equal(ContractJson.Read<ShiftPlanRequest>(Example("shift-plan-request"), "ShiftPlanRequest"), result.Request with { }, new RequestComparer());
    }

    [Theory]
    [InlineData("staff")]
    [InlineData("score")]
    [InlineData("moves")]
    [InlineData("breaks")]
    [InlineData("duplicate")]
    [InlineData("length")]
    [InlineData("timeout")]
    public void Forged_plans_are_rejected(string kind)
    {
        var result = Result();
        var first = result.Shifts[0];
        result = kind switch
        {
            "staff" => result with { Checkpoints = [result.Checkpoints[0] with { Staff = [.. result.Checkpoints[0].Staff.Select((count, t) => t == 0 ? count + 1 : count)] }, .. result.Checkpoints.Skip(1)] },
            "score" => result with { Score = result.Score - 1 },
            "moves" => result with { Moves = result.Moves + 1 },
            "breaks" => result with { Shifts = [first with { Breaks = [99] }, .. result.Shifts.Skip(1)] },
            "duplicate" => result with { Shifts = [.. result.Shifts, first] },
            "length" => result with { Request = result.Request with { Rules = result.Request.Rules with { MaxShiftSlots = result.Request.Rules.MinShiftSlots } } },
            "timeout" => result with { Status = "timeout", SolverStatus = "UNKNOWN", TimedOut = true },
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
        Assert.False(ShiftPlanValidation.Valid(result));
    }

    [Fact]
    public void A_shift_without_its_break_is_rejected()
    {
        var request = Result().Request;
        var checkpoint = request.Checkpoints[0].CheckpointId;
        var shift = new StaffShift(request.Staff[0].StaffMemberId, 0, request.Rules.BreakAfterSlots + 1, [],
            [new ShiftSegment(checkpoint, 0, request.Rules.BreakAfterSlots + 1)]);
        var (plans, duty, moves, score) = ShiftPlanValidation.Evaluate(request, [shift]);
        var forged = new ShiftPlanResult("feasible", "FEASIBLE", true, score, 0, duty, moves, [shift], plans, request, Result().Objective, 0);
        Assert.False(ShiftPlanValidation.Valid(forged));
    }

    [Fact]
    public void Scenario_shifts_become_engine_steps_and_a_move_starts_after_the_transfer()
    {
        var layout = Layout();
        var inputs = new ScenarioInputs
        {
            StartsAtUtc = new DateTime(2026, 10, 15, 5, 0, 0, DateTimeKind.Utc), DurationMinutes = 240,
            StaffShifts = new ScenarioStaffShifts(15, 5, [new ScenarioStaffShift(Person,
                [new ScenarioShiftSegment(ControlA, 15, 60), new ScenarioShiftSegment(ControlB, 60, 120), new ScenarioShiftSegment(ControlB, 135, 180)])])
        };
        StaffShiftScenarios.Validate(inputs, layout);
        var schedules = StaffShiftScenarios.Schedules(inputs, layout, KpiConventions.Engine)!.ToDictionary(item => item.CheckpointId, item => item.Steps);
        Assert.Equal([new StaffStep(0, 0), new StaffStep(15, 1), new StaffStep(60, 0)], schedules[ControlA]);
        // Straight from A: serving at B from minute 65. After the break at 120–135 there is no transfer.
        Assert.Equal([new StaffStep(0, 0), new StaffStep(65, 1), new StaffStep(120, 0), new StaffStep(135, 1), new StaffStep(180, 0)], schedules[ControlB]);
        Assert.Equal(1, StaffShiftScenarios.StaffCount(inputs, ControlA));
        Assert.Throws<ScenarioValidationException>(() => StaffShiftScenarios.Schedules(inputs, layout, null));
        Assert.Null(StaffShiftScenarios.Schedules(inputs with { StaffShifts = null }, layout, KpiConventions.Engine));
    }

    [Fact]
    public void Scenario_shifts_must_fit_the_layout_and_the_day()
    {
        var layout = Layout();
        var shift = new ScenarioStaffShift(Person, [new ScenarioShiftSegment(ControlA, 0, 60), new ScenarioShiftSegment(ControlB, 30, 90)]);
        var inputs = new ScenarioInputs { DurationMinutes = 90, StaffShifts = new ScenarioStaffShifts(15, 20, [shift, shift]) };
        var errors = Assert.Throws<ScenarioValidationException>(() => StaffShiftScenarios.Validate(inputs, layout)).Errors;
        Assert.Contains("inputs.staffShifts", errors.Keys);
        Assert.Contains("inputs.staffShifts.transferMinutes", errors.Keys);
        Assert.Contains("inputs.staffShifts.shifts[0].segments[1]", errors.Keys);
        Assert.Contains("inputs.staffShifts.shifts[1].staffMemberId", errors.Keys);
    }

    private static SceneDetails Layout() => new(new SceneSummary(Guid.NewGuid(), "DEMO", "Demo", "", true), [],
        [new Checkpoint { Id = ControlA, TerminalId = Terminal, Name = "A", CapacityPerMinute = 1 },
         new Checkpoint { Id = ControlB, TerminalId = Terminal, Name = "B", CapacityPerMinute = 1 }], [],
        [new StaffMember { Id = Person, TerminalId = Terminal }], []);

    private sealed class RequestComparer : IEqualityComparer<ShiftPlanRequest>
    {
        public bool Equals(ShiftPlanRequest? x, ShiftPlanRequest? y) =>
            JsonSerializer.Serialize(x, ContractJson.Options) == JsonSerializer.Serialize(y, ContractJson.Options);
        public int GetHashCode(ShiftPlanRequest value) => 0;
    }
}
