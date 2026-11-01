using AeroSynapse.Application.Contracts;
using AeroSynapse.Application.Management;
using AeroSynapse.Application.Storage;
using AeroSynapse.Domain.DigitalTwin;
using Xunit;

namespace AeroSynapse.Management.Tests;

public sealed class RunOptimizationTests
{
    private static readonly Guid Hot = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Calm = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Desk = Guid.Parse("00000000-0000-0000-0000-000000000009");
    private static readonly Dictionary<Guid, string> Names = new() { [Hot] = "CP-1", [Calm] = "CP-2" };

    private static KpiSnapshot At(Guid zone, int minute, int queued, double? p95) => new("1", Guid.Empty, zone, "checkpoint", minute, minute, 0, "",
        0, 0, 0, queued, 0, 0, 0, 0, 0, 0, 0, queued, 0, 0, 0, 0, null, null, p95, null, null, null, null, null, 0, 0, 0, 0, null);

    [Fact]
    public void Options_relieve_the_longest_queue_and_skip_controls_with_a_shift_plan()
    {
        CheckpointInput[] checkpoints = [new(Hot, 1, 2), new(Calm, 1, 3)];
        KpiSnapshot[] reference = [At(Hot, 60, 40, 18), At(Calm, 60, 0, 1)];
        var candidates = RunOptimizationPlanner.Candidates(checkpoints, reference, Names, new HashSet<Guid>());
        Assert.Equal(["move", "add-1", "add-2", "save"], candidates.Select(c => c.Id));
        Assert.Equal("Mută un operator de la CP-2 la CP-1", candidates[0].Action);
        Assert.Equal(0, candidates[0].OperatorDelta);
        var applied = RunOptimizationPlanner.Apply(new("v", Guid.Empty, Guid.Empty, 1, Guid.Empty, default, "UTC", 60, 1, 10, 1, checkpoints,
            new PassengerGenerationRequest("g", 1, 60, checkpoints, [], [])), Guid.NewGuid(), candidates[0]);
        Assert.Equal([3, 2], applied.Checkpoints.Select(c => c.StaffCount));
        Assert.Equal(applied.Checkpoints, applied.PassengerGeneration!.Checkpoints);

        Assert.Empty(RunOptimizationPlanner.Candidates(checkpoints, reference, Names, new HashSet<Guid> { Hot, Calm }));
    }

    [Fact]
    public void Without_the_model_the_shortest_wait_wins_and_a_free_saving_comes_first()
    {
        var baseline = new AgentImpactSide(10, 20, 40, null);
        var move = new OptimizationCandidate("move", "move", "m", new Dictionary<Guid, int> { [Calm] = -1, [Hot] = 1 });
        var add = new OptimizationCandidate("add-1", "add", "a", new Dictionary<Guid, int> { [Hot] = 1 });
        var save = new OptimizationCandidate("save", "save", "s", new Dictionary<Guid, int> { [Calm] = -1 });
        Assert.Equal("move", RunOptimizationPlanner.Recommend(baseline, [(move, baseline with { MeanWaitMinutes = 4 }), (add, baseline with { MeanWaitMinutes = 4 })]));
        // Passengers first: a real improvement beats a saving; a saving wins only when nothing shortens the wait.
        Assert.Equal("add-1", RunOptimizationPlanner.Recommend(baseline, [(add, baseline with { MeanWaitMinutes = 4 }), (save, baseline)]));
        Assert.Equal("save", RunOptimizationPlanner.Recommend(baseline, [(add, baseline with { MeanWaitMinutes = 9.8 }), (save, baseline)]));
        // A free move nearly as good as hiring (within 5% of the best wait) is preferred; a clearly better hire still wins.
        Assert.Equal("move", RunOptimizationPlanner.Recommend(baseline, [(add, baseline with { MeanWaitMinutes = 4 }), (move, baseline with { MeanWaitMinutes = 4.1 })]));
        Assert.Equal("add-1", RunOptimizationPlanner.Recommend(baseline, [(add, baseline with { MeanWaitMinutes = 4 }), (move, baseline with { MeanWaitMinutes = 8 })]));
        Assert.Null(RunOptimizationPlanner.Recommend(baseline, [(save, baseline with { MeanWaitMinutes = 15 })]));
        // A saving that makes waits noticeably longer is not recommended over a real improvement.
        Assert.Equal("add-1", RunOptimizationPlanner.Recommend(baseline, [(save, baseline with { MeanWaitMinutes = 14.8 }), (add, baseline with { MeanWaitMinutes = 4 })]));

        // Saving at +8% is no longer offered as an improvement, and its undo (-7.4%) would be: never both, so no ping-pong.
        Assert.Null(RunOptimizationPlanner.Recommend(baseline, [(save, baseline with { MeanWaitMinutes = 10.8 })]));
        Assert.Equal("save", RunOptimizationPlanner.Recommend(baseline, [(save, baseline with { MeanWaitMinutes = 10.5 })]));
        var afterSaving = baseline with { MeanWaitMinutes = 10.5 };
        Assert.Null(RunOptimizationPlanner.Recommend(afterSaving, [(add, baseline)]));
        Assert.Null(RunOptimizationPlanner.Recommend(baseline with { MeanWaitMinutes = null }, [(save, baseline with { MeanWaitMinutes = 4 })]));
    }

    [Fact]
    public void Applying_an_option_moves_frees_and_adds_people_and_says_when_nobody_is_free()
    {
        var terminal = Guid.NewGuid();
        Guid a = Guid.Parse("00000000-0000-0000-0000-00000000000a"), b = Guid.Parse("00000000-0000-0000-0000-00000000000b"),
            spare = Guid.Parse("00000000-0000-0000-0000-00000000000c");
        var layout = new SceneDetails(new(Guid.Empty, "", "", "", true), [],
            [new Checkpoint { Id = Hot, Code = "CP-1", TerminalId = terminal }, new Checkpoint { Id = Calm, Code = "CP-2", TerminalId = terminal }], [],
            new[] { a, b, spare }.Select(id => new StaffMember { Id = id, TerminalId = terminal }).ToArray(), []);
        var inputs = new ScenarioInputs { StaffAllocations = [new(a, Calm), new(b, Calm)] };
        int[] Staff(ScenarioInputs value) => [value.StaffAllocations.Count(x => x.CheckpointId == Hot), value.StaffAllocations.Count(x => x.CheckpointId == Calm)];

        var moved = RunOptimizationPlanner.Staff(inputs, layout, new Dictionary<Guid, int> { [Calm] = -1, [Hot] = 1 }).Inputs!;
        Assert.Equal(new StaffAllocation(b, Hot), moved.StaffAllocations.Single(x => x.CheckpointId == Hot));
        Assert.Equal([0, 1], Staff(RunOptimizationPlanner.Staff(inputs, layout, new Dictionary<Guid, int> { [Calm] = -1 }).Inputs!));
        Assert.Equal(new StaffAllocation(spare, Hot), RunOptimizationPlanner.Staff(inputs, layout, new Dictionary<Guid, int> { [Hot] = 1 }).Inputs!.StaffAllocations[^1]);

        var (none, issue) = RunOptimizationPlanner.Staff(inputs, layout, new Dictionary<Guid, int> { [Hot] = 2 });
        Assert.Null(none);
        Assert.Equal("Nu există niciun operator de securitate liber pentru CP-1: toți oamenii scenariului sunt deja alocați.", issue);
        Assert.NotNull(RunOptimizationPlanner.Staff(inputs, layout, new Dictionary<Guid, int> { [Hot] = -1 }).Issue);

        // A scenario with its own team adds only its own unassigned operators, never a layout slot it does not have.
        var team = inputs with { Operators = [new(a, "Ana", "security"), new(b, "Bogdan", "security")] };
        Assert.NotNull(RunOptimizationPlanner.Staff(team, layout, new Dictionary<Guid, int> { [Hot] = 1 }).Issue);
        Assert.Null(RunOptimizationPlanner.Staff(team, layout, new Dictionary<Guid, int> { [Calm] = -1, [Hot] = 1 }).Issue);
        var spareInTeam = team with { Operators = [.. team.Operators!, new(spare, "Cora", "security")] };
        Assert.Equal(new StaffAllocation(spare, Hot), RunOptimizationPlanner.Staff(spareInTeam, layout, new Dictionary<Guid, int> { [Hot] = 1 }).Inputs!.StaffAllocations[^1]);
    }

    [Fact]
    public void A_check_in_bottleneck_gets_desk_options_first_within_its_desks_and_people()
    {
        CheckpointInput[] checkpoints = [new(Hot, 1, 2), new(Calm, 1, 3)];
        KpiSnapshot[] reference = [At(Hot, 60, 10, 4), At(Calm, 60, 0, 1)];
        var candidates = RunOptimizationPlanner.Candidates(checkpoints, reference, Names, new HashSet<Guid>(), new("CHECK-IN", 8, 12, 25, 94));
        Assert.Equal(["desk-1", "desk-2", "move", "add-1", "add-2", "save"], candidates.Select(c => c.Id));
        Assert.Equal(2, candidates[1].OperatorDelta);
        // A security queue longer than the desks' keeps the controls first.
        Assert.Equal("move", RunOptimizationPlanner.Candidates(checkpoints, [At(Hot, 60, 120, 30)], Names, new HashSet<Guid>(), new("CHECK-IN", 8, 12, 25, 94))[0].Id);

        var request = RunOptimizationPlanner.Apply(new("v", Guid.Empty, Guid.Empty, 1, Guid.Empty, default, "UTC", 60, 1, 10, 1, checkpoints,
            new PassengerGenerationRequest("g", 1, 60, checkpoints, [], []), CheckIn: new(Desk, 8, 0.5, 0.4, 1.5)), Guid.NewGuid(), candidates[1]);
        Assert.Equal(10, request.CheckIn!.StaffCount);
        Assert.Equal(checkpoints, request.Checkpoints);

        var layout = new SceneDetails(new(Guid.Empty, "", "", "", true), [], [], [], [], [],
            [new CheckInArea { Id = Desk, Code = "CHECK-IN", DeskCount = 9 }]);
        var counted = new ScenarioInputs { CheckIn = new(Desk, 8, 0.4m) };
        // Without a team the desks are a count, up to the area's desks.
        var opened = RunOptimizationPlanner.Staff(counted, layout, new Dictionary<Guid, int>(), 1).Inputs!;
        Assert.Equal(9, opened.CheckIn!.StaffCount);
        Assert.Equal("La CHECK-IN sunt deja deschise 8 din cele 9 ghișee.", RunOptimizationPlanner.Staff(counted, layout, new Dictionary<Guid, int>(), 2).Issue);

        // With a team every check-in operator is already at a desk, so no desk can open without new people.
        var team = counted with { Operators = Enumerable.Range(1, 8).Select(i => new ScenarioOperator(Guid.NewGuid(), $"C{i}", "checkIn")).ToArray() };
        Assert.Equal("Toți cei 8 operatori de check-in ai scenariului sunt deja la ghișee; pentru mai multe ghișee adaugă operatori de check-in în Scenario Studio.",
            RunOptimizationPlanner.Staff(team, layout, new Dictionary<Guid, int>(), 1).Issue);
        // Closing a desk drops its operator, so the team still matches the open desks.
        var closed = RunOptimizationPlanner.Staff(team, layout, new Dictionary<Guid, int>(), -1).Inputs!;
        OperatorScenarios.Validate(closed, layout);
        Assert.Equal(7, closed.Operators!.Count);
    }

    [Fact]
    public void Check_in_waits_count_in_the_measure_of_each_option()
    {
        CheckInSnapshot Desk(int minute, int queued, double? mean) =>
            new(RunOptimizationTests.Desk, "checkIn", minute, 40, queued, 0, 40 - queued, 0, queued, mean, mean, mean, 8, 0, null);
        var side = AgentImpactSummary.From([At(Hot, 60, 5, 6) with { WaitSampleCount = 20, MeanWaitMinutes = 2 }], [Desk(30, 94, 1), Desk(60, 0, 10)]);
        // 20 passengers waited 2 minutes at security; the 40 who passed the desks waited 10 there: (40 + 400) / 20.
        Assert.Equal(22, side.MeanWaitMinutes);
        Assert.Equal(94, side.MaxQueue);
        Assert.Equal(10, side.MaxP95WaitMinutes);
    }

    [Fact]
    public void The_bottleneck_is_the_busiest_moment_check_in_included_earliest_on_a_tie_and_none_without_queues()
    {
        CheckInSnapshot Desk(int minute, int queued) => new(RunOptimizationTests.Desk, "checkIn", minute, 40, queued, 0, 0, 0, queued, null, null, null, 8, 0, null);
        var names = new Dictionary<Guid, string>(Names) { [RunOptimizationTests.Desk] = "CHECK-IN" };
        // Like the run summary's headline: the single longest queue at any minute, wherever it formed.
        Assert.Equal(new OptimizationPeak("CP-1", 26, 11), OptimizationBottleneck.Of([At(Hot, 11, 26, 4), At(Calm, 30, 14, 2)], [Desk(7, 14)], names));
        Assert.Equal(new OptimizationPeak("CHECK-IN", 14, 7), OptimizationBottleneck.Of([At(Hot, 9, 14, 4)], [Desk(7, 14)], names));
        Assert.Null(OptimizationBottleneck.Of([At(Hot, 9, 0, 0)], null, names));
    }

    private static readonly OptimizationCandidate Move = new("move", "move", "Mută un operator de la CP-2 la CP-1", new Dictionary<Guid, int> { [Calm] = -1, [Hot] = 1 });
    private static readonly OptimizationCandidate AddOne = new("add-1", "add", "Adaugă un operator la CP-1", new Dictionary<Guid, int> { [Hot] = 1 });
    private static readonly OptimizationCandidate Save = new("save", "save", "Retrage un operator de la CP-2", new Dictionary<Guid, int> { [Calm] = -1 });
    private static readonly string[] Places = ["CP-1", "CP-2", "CHECK-IN"];
    // The run the user reported: 9 s of mean wait, longest queue at check-in; one more operator at CP-1 is the only gain.
    private static readonly AgentImpactSide Reference = new(0.15, 2, 14, null);
    private static readonly (OptimizationCandidate, AgentImpactSide)[] Reported =
        [(Move, Reference with { MeanWaitMinutes = 0.18 }), (AddOne, Reference with { MeanWaitMinutes = 0.135 }), (Save, Reference with { MeanWaitMinutes = 0.197 })];

    [Fact]
    public void The_brief_digests_every_option_and_says_why_the_recommended_one_wins()
    {
        var brief = RunOptimizationPlanner.Brief(Reference, Reported, new("CHECK-IN", 14, 7));
        Assert.Equal(["worse", "better", "worse"], brief.Options.Select(o => o.Verdict));
        var add = brief.Options[1];
        Assert.True(add.Recommended);
        Assert.Equal("9 s → 8 s (-10%)", add.Wait);
        Assert.Equal("14 → 14", add.MaxQueue);
        Assert.Equal("un operator în plus", add.Staff);
        Assert.Equal("Pasagerii așteaptă în medie cu 1 s mai puțin, cu un operator în plus.", add.Facts);
        Assert.Equal("Pasagerii așteaptă în medie cu 3 s mai mult, dar se economisește un operator.", brief.Options[2].Facts);
        Assert.Equal("Adaugă un operator la CP-1: scurtează cel mai mult așteptarea medie dintre variantele testate.", brief.Recommendation);
        Assert.Equal("Cea mai lungă coadă: 14 pasageri la CHECK-IN, în minutul 7. Recomandăm „Adaugă un operator la CP-1”: "
            + "pasagerii așteaptă în medie cu 1 s mai puțin, cu un operator în plus.", brief.Summary);

        // A saving within 5% is "same" and wins; nothing worth it leaves no recommendation, so the panel says optimal.
        var saving = RunOptimizationPlanner.Brief(Reference, [(Save, Reference with { MeanWaitMinutes = 0.155 })], new("CP-1", 32, 14));
        Assert.Equal("same", saving.Options.Single().Verdict);
        Assert.Equal("Retrage un operator de la CP-2: economisește personal fără să lungească vizibil așteptarea.", saving.Recommendation);
        Assert.Equal("Cea mai lungă coadă: 32 de pasageri la CP-1, în minutul 14.", saving.Where);
        var none = RunOptimizationPlanner.Brief(Reference, [(Save, Reference with { MeanWaitMinutes = 0.2 })], null);
        Assert.Null(none.Recommendation);
        Assert.Equal("Nu s-a format nicio coadă. Nicio variantă testată nu scurtează vizibil așteptarea.", none.Summary);
        Assert.True(RunOptimizationPlanner.Compose(Reference, [], [(Save, Reference with { MeanWaitMinutes = 0.2 })], none, new("unavailable", "m"), Places).Optimal);
    }

    [Fact]
    public void A_model_sentence_that_contradicts_the_numbers_is_replaced_by_the_facts_and_a_consistent_one_is_kept()
    {
        var brief = RunOptimizationPlanner.Brief(Reference, Reported, new("CHECK-IN", 14, 7));
        var advice = new OptimizationAdvice("chosen", "qwen2.5:7b", "Adaugă un operator la CP-1: scurtează așteptarea medie a pasagerilor.",
        [
            new("add-1", "Pasagerii așteaptă puțin mai puțin, cu un operator în plus."),
            // The sentence the user saw for −1 operator at +31% wait.
            new("save", "Retragerea nu ajută, cozi rămân lungi. Pasagerii așteaptă mai mult. Cozi rămân lungi, dar așteptarea este mai scurtă."),
            new("move", "Pasagerii așteaptă mai mult, fără personal în plus.")
        ]);
        var panel = RunOptimizationPlanner.Compose(Reference, [], Reported, brief, advice, Places);
        var text = panel.Options.ToDictionary(o => o.Id, o => o.Explanation);
        Assert.Equal("Pasagerii așteaptă puțin mai puțin, cu un operator în plus.", text["add-1"]);
        Assert.Equal("Pasagerii așteaptă în medie cu 3 s mai mult, dar se economisește un operator.", text["save"]);
        Assert.Equal("Pasagerii așteaptă mai mult, fără personal în plus.", text["move"]);
        Assert.Equal("add-1", panel.Options[0].Id);
        Assert.True(panel.Options[0].Recommended);
        // The card's title is the action itself: the panel has no other name for an option.
        Assert.Equal("Adaugă un operator la CP-1", panel.Options[0].Action);
        Assert.Equal("Cea mai lungă coadă: 14 pasageri la CHECK-IN, în minutul 7. Adaugă un operator la CP-1: scurtează așteptarea medie a pasagerilor.", panel.Summary);

        // Advice that does not name the recommended action as written, or no answer at all, leaves the code's summary.
        var vague = RunOptimizationPlanner.Compose(Reference, [], Reported, brief, advice with { Advice = "Bottleneck la CHECK-IN, cu cozi lungi de 14 de persoane." }, Places);
        Assert.Equal(brief.Summary, vague.Summary);
        var silent = RunOptimizationPlanner.Compose(Reference, [], Reported, brief, new("timeout", "qwen2.5:7b"), Places);
        Assert.Equal(brief.Summary, silent.Summary);
        Assert.All(silent.Options, o => Assert.Equal(brief.Options.Single(f => f.Id == o.Id).Facts, o.Explanation));
    }

    [Theory]
    [InlineData("better", 1, "Pasagerii așteaptă mai puțin, cu un operator în plus.", true)]
    [InlineData("better", 1, "Așteptarea scade cu 10 secunde.", false)] // a number the card already shows, maybe wrongly
    [InlineData("better", 1, "Pasagerii așteaptă mai puțin la CP-2.", false)] // a place the option does not touch
    [InlineData("better", 1, "Este varianta cu un operator în plus.", false)] // says nothing about the wait
    [InlineData("better", 1, "Pasagerii asteapta mai mult.", false)] // diacritics left out change nothing
    [InlineData("worse", 0, "Pasagerii așteaptă mai mult, deși nu se adaugă personal.", true)]
    [InlineData("worse", 0, "Mutarea ajută, pasagerii așteaptă mai mult.", false)]
    [InlineData("worse", -1, "Pasagerii așteaptă mai mult, dar se economisește un operator.", true)]
    [InlineData("worse", -1, "Pasagerii așteaptă mai mult, cu un operator în plus.", false)] // staff the other way
    [InlineData("same", -1, "Așteptarea rămâne practic aceeași și se economisește un operator.", true)]
    [InlineData("same", -1, "Pasagerii așteaptă mai puțin și se economisește un operator.", false)]
    [InlineData("same", -1, "Așteptarea nu crește vizibil, iar un operator este economisit.", true)]
    [InlineData("same", 0, "Recomandată: așteptarea rămâne la fel.", false)] // only the recommended option is called so
    public void A_sentence_agrees_only_with_the_verdict_and_staff_of_its_option(string verdict, int staff, string text, bool agrees)
    {
        var facts = new OptimizationFacts("x", "Adaugă un operator la CP-1", false, verdict, "9 s → 8 s (-10%)", "14 → 14", null, "", "");
        Assert.Equal(agrees, OptimizationText.Agrees(text, facts, 0, staff, [facts.Action, "CHECK-IN"], Places));
    }
}
