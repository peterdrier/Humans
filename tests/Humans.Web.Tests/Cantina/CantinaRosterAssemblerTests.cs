using AwesomeAssertions;
using Humans.Application.Services.Cantina.Dtos;
using Humans.Testing;
using Humans.Web.Cantina;
using NodaTime;

namespace Humans.Web.Tests.Cantina;

/// <summary>
/// Tests for <see cref="CantinaRosterAssembler.SortForDisplay"/>, the
/// presentation-layer sort that the Cantina Weekly Roster controller
/// applies before handing the DTO to the view or CSV writer. Moved here
/// from <c>CantinaRosterServiceTests</c> when display sort migrated out
/// of the Application service per
/// <c>memory/architecture/display-sort-in-controllers.md</c>.
/// </summary>
public class CantinaRosterAssemblerTests
{
    private static readonly LocalDate GateOpening = new(2026, 7, 7);

    private static RosterPersonDto P(
        string burnerName,
        LocalDate arrivesOn,
        string? dietary = null,
        IReadOnlyList<string>? allergies = null,
        IReadOnlyList<string>? intolerances = null,
        string? allergyOther = null,
        string? intoleranceOther = null) =>
        new(
            UserId: Guid.NewGuid(),
            BurnerName: burnerName,
            ArrivesOn: arrivesOn,
            NoShift: Array.Empty<LocalDate>(),
            DietaryPreference: dietary,
            Allergies: allergies ?? Array.Empty<string>(),
            AllergyOtherText: allergyOther,
            Intolerances: intolerances ?? Array.Empty<string>(),
            IntoleranceOtherText: intoleranceOther);

    [HumansFact]
    public void SortForDisplay_OrderedByFirstArrivalThenAllergiesThenDietaryThenName()
    {
        // Fixture (week day 0 = Tue Jul 7 2026, day 1 = Wed Jul 8 2026):
        //   Alice   — day 0 (Tue), Vegan,      no allergies
        //   Bob     — day 0 (Tue), Omnivore,   Peanut allergy
        //   Charlie — day 1 (Wed), Omnivore,   no allergies
        //   Donna   — day 1 (Wed), Vegetarian, no allergies
        //
        // Expected order:
        //   Bob     (day 0, has allergy → first within day 0)
        //   Alice   (day 0, Vegan — only one non-allergy person on day 0)
        //   Charlie (day 1, Omnivore — first in canonical dietary order)
        //   Donna   (day 1, Vegetarian — second in canonical dietary order)
        var input = new List<RosterPersonDto>
        {
            P("Alice", GateOpening, dietary: "Vegan"),
            P("Bob", GateOpening, dietary: "Omnivore", allergies: new[] { "Peanut" }),
            P("Charlie", GateOpening.PlusDays(1), dietary: "Omnivore"),
            P("Donna", GateOpening.PlusDays(1), dietary: "Vegetarian")
        };

        var sorted = CantinaRosterAssembler.SortForDisplay(input);

        sorted.Select(p => p.BurnerName).Should().Equal("Bob", "Alice", "Charlie", "Donna");
    }

    [HumansFact]
    public void SortForDisplay_NameTiebreakWhenArrivalAllergyAndDietaryMatch()
    {
        // Two humans, same arrival day, same dietary (Omnivore), no allergies.
        // BurnerName cultural-collation asc should be the final tiebreaker.
        var input = new List<RosterPersonDto>
        {
            P("Zane", GateOpening, dietary: "Omnivore"),
            P("Anne", GateOpening, dietary: "Omnivore")
        };

        var sorted = CantinaRosterAssembler.SortForDisplay(input);

        sorted.Select(p => p.BurnerName).Should().Equal("Anne", "Zane");
    }

    [HumansFact]
    public void SortForDisplay_UnansweredDietarySortsAfterAnswered()
    {
        // Same arrival day, same allergy status (none). The one with a known
        // dietary (Omnivore) sorts before the unanswered one even when the
        // unanswered one's BurnerName is alphabetically earlier.
        var input = new List<RosterPersonDto>
        {
            P("Aaron", GateOpening, dietary: null),
            P("Bertha", GateOpening, dietary: "Omnivore")
        };

        var sorted = CantinaRosterAssembler.SortForDisplay(input);

        sorted.Select(p => p.BurnerName).Should().Equal("Bertha", "Aaron");
    }

    [HumansFact]
    public void SortForDisplay_BurnerNameTiebreaker_UsesCulturalCollation_NotOrdinal()
    {
        // Smoke check: lower-case "alice" sorts BEFORE upper-case "Bob"
        // under cultural-collation rules (case-insensitive), whereas
        // StringComparer.Ordinal would put 'B' (0x42) before 'a' (0x61).
        // This proves we're not using Ordinal — the more elaborate
        // diacritic semantics (Spanish ñ vs n etc.) are runtime-culture
        // dependent and not asserted here.
        var input = new List<RosterPersonDto>
        {
            P("Bob", GateOpening),
            P("alice", GateOpening)
        };

        var sorted = CantinaRosterAssembler.SortForDisplay(input);

        sorted.Select(p => p.BurnerName).Should().Equal("alice", "Bob");
    }

    [HumansFact]
    public void SortForDisplay_EmptyInput_ReturnsEmpty()
    {
        var sorted = CantinaRosterAssembler.SortForDisplay(Array.Empty<RosterPersonDto>());
        sorted.Should().BeEmpty();
    }

    [HumansFact]
    public void SortForDisplay_SingleItem_ReturnsAsIs()
    {
        var only = P("Solo", GateOpening, dietary: "Vegan");
        var sorted = CantinaRosterAssembler.SortForDisplay(new[] { only });
        sorted.Should().HaveCount(1);
        sorted[0].BurnerName.Should().Be("Solo");
    }

    [HumansFact]
    public void HasAnyAllergyOrIntolerance_TrueForChipsOrFreeText()
    {
        CantinaRosterAssembler.HasAnyAllergyOrIntolerance(P("A", GateOpening, allergies: new[] { "Peanut" }))
            .Should().BeTrue();
        CantinaRosterAssembler.HasAnyAllergyOrIntolerance(P("B", GateOpening, intolerances: new[] { "Lactose" }))
            .Should().BeTrue();
        CantinaRosterAssembler.HasAnyAllergyOrIntolerance(P("C", GateOpening, allergyOther: "MSG"))
            .Should().BeTrue();
        CantinaRosterAssembler.HasAnyAllergyOrIntolerance(P("D", GateOpening, intoleranceOther: "Coffee"))
            .Should().BeTrue();
        CantinaRosterAssembler.HasAnyAllergyOrIntolerance(P("E", GateOpening))
            .Should().BeFalse();
    }

    [HumansFact]
    public void DietaryPriority_PutsUnansweredLast()
    {
        CantinaRosterAssembler.DietaryPriority("Omnivore").Should().BeLessThan(
            CantinaRosterAssembler.DietaryPriority("Vegetarian"));
        CantinaRosterAssembler.DietaryPriority("Vegetarian").Should().BeLessThan(
            CantinaRosterAssembler.DietaryPriority(null));
        CantinaRosterAssembler.DietaryPriority("UnknownLegacy").Should().BeLessThan(
            CantinaRosterAssembler.DietaryPriority(null));
        CantinaRosterAssembler.DietaryPriority("UnknownLegacy").Should().BeGreaterThan(
            CantinaRosterAssembler.DietaryPriority("Pescatarian"));
    }

    // ---- Daily matrix assembler tests --------------------------------------

    private static DailyPersonRowDto DP(string burnerName) =>
        new(
            UserId: Guid.NewGuid(),
            BurnerName: burnerName,
            DietaryPreference: null,
            Allergies: new HashSet<string>(StringComparer.Ordinal),
            AllergyOtherText: null,
            Intolerances: new HashSet<string>(StringComparer.Ordinal),
            IntoleranceOtherText: null);

    private static DailyMatrixDto Matrix(IReadOnlyList<DailyPersonRowDto> people) =>
        new(
            DayOffset: 0,
            CalendarDate: GateOpening,
            EventTodayDate: GateOpening,
            EventName: "Elsewhere 2026",
            WeekStartOffset: -1,
            TotalOnSite: people.Count,
            UnansweredCount: people.Count,
            DietaryBreakdown: new Dictionary<string, int>(StringComparer.Ordinal),
            AllergyRollup: Array.Empty<RollupItemDto>(),
            AllergyOtherEntries: Array.Empty<string>(),
            IntoleranceRollup: Array.Empty<RollupItemDto>(),
            IntoleranceOtherEntries: Array.Empty<string>(),
            People: people);

    [HumansFact]
    public void WithSortedPeople_Daily_AlphabeticalByBurnerName()
    {
        var input = Matrix(new[] { DP("Charlie"), DP("Alice"), DP("Bob") });

        var sorted = CantinaRosterAssembler.WithSortedPeople(input);

        sorted.People.Select(p => p.BurnerName).Should().Equal("Alice", "Bob", "Charlie");
        // Other fields preserved.
        sorted.DayOffset.Should().Be(input.DayOffset);
        sorted.WeekStartOffset.Should().Be(input.WeekStartOffset);
    }

    [HumansFact]
    public void WithSortedPeople_Daily_CulturalCollation_CaseInsensitive()
    {
        // Smoke check: lower-case "alice" must sort BEFORE upper-case "Bob"
        // (cultural-collation, case-insensitive). Ordinal would put 'B' (0x42)
        // before 'a' (0x61). The daily matrix sort matches the weekly view's
        // BurnerName tiebreaker on this dimension.
        var input = Matrix(new[] { DP("Bob"), DP("alice") });

        var sorted = CantinaRosterAssembler.WithSortedPeople(input);

        sorted.People.Select(p => p.BurnerName).Should().Equal("alice", "Bob");
    }

    [HumansFact]
    public void WithSortedPeople_Daily_EmptyAndSingle_ReturnAsIs()
    {
        var empty = Matrix(Array.Empty<DailyPersonRowDto>());
        CantinaRosterAssembler.WithSortedPeople(empty).People.Should().BeEmpty();

        var single = Matrix(new[] { DP("Solo") });
        CantinaRosterAssembler.WithSortedPeople(single).People.Should().HaveCount(1);
    }
}
