using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Contract.Tests;

// Operators the user entered: security slots of the layout and check-in desk staff.
public sealed class OperatorTests
{
    private static readonly StaffMember Slot = new() { Id = Guid.NewGuid(), Code = "STAFF-1" };
    private static readonly CheckInArea Desks = new() { Id = Guid.NewGuid(), DeskCount = 8, ServiceSeconds = 120, WalkingSeconds = 90 };
    private static readonly SceneDetails Layout = new(new(Guid.NewGuid(), "DEMO", "Demo", "", true), [], [], [], [Slot], [], [Desks]);

    private static ScenarioInputs Inputs(params ScenarioOperator[] operators) => new()
    {
        Operators = operators, CheckIn = new(Desks.Id, operators.Count(person => person.Role == "checkIn"), .4m)
    };
    private static readonly ScenarioOperator[] Team = [new(Slot.Id, "Ana", "security"), new(Guid.NewGuid(), "Bogdan", "checkIn")];

    [Fact]
    public void Operators_stay_within_the_layout_and_the_open_desks()
    {
        OperatorScenarios.Validate(Inputs(Team), Layout);
        static string Field(ScenarioInputs inputs) =>
            Assert.Single(Assert.Throws<ScenarioValidationException>(() => OperatorScenarios.Validate(inputs, Layout)).Errors.Keys);
        Assert.Equal("inputs.operators[0].id", Field(Inputs(new ScenarioOperator(Guid.NewGuid(), "Ana", "security"))));
        Assert.Equal("inputs.operators[0].role", Field(Inputs(new ScenarioOperator(Guid.NewGuid(), "Ioana", "gate"))));
        Assert.Equal("inputs.operators[0].name", Field(Inputs(new ScenarioOperator(Guid.NewGuid(), " ", "checkIn"))));
        Assert.Equal("inputs.checkIn.staffCount", Field(Inputs(Team) with { CheckIn = new(Desks.Id, 3, .4m) }));
        Assert.Equal("inputs.checkIn", Field(Inputs(Team) with { CheckIn = null }));
    }

    [Fact]
    public void Resizing_check_in_keeps_the_first_operators_and_names_new_ones()
    {
        Assert.Equal(["Ana", "Bogdan", "Operator check-in 2"], OperatorScenarios.WithDesks(Team, 2)!.Select(person => person.Name));
        Assert.Equal(["Ana"], OperatorScenarios.WithDesks(Team, 0)!.Select(person => person.Name));
        Assert.Null(OperatorScenarios.WithDesks(null, 3));
    }
}
