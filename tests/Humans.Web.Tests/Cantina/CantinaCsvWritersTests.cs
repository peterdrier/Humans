using System.Text;
using AwesomeAssertions;
using Humans.Application.Services.Cantina.Dtos;
using Humans.Web.Cantina;
using NodaTime;

namespace Humans.Web.Tests.Cantina;

/// <summary>
/// Pins the Cantina CSV export layout and safety properties through the
/// CsvHelper conversion: BOM + CRLF, section structure, RFC 4180 quoting,
/// and OWASP injection escaping of user-controlled profile text.
/// </summary>
public sealed class CantinaCsvWritersTests
{
    private static readonly LocalDate WeekStart = new(2026, 7, 7);

    private static RosterPersonDto Person(
        string burnerName,
        string? dietary = "Omnivore",
        IReadOnlyList<string>? allergies = null,
        string? allergyOther = null) =>
        new(
            UserId: Guid.NewGuid(),
            BurnerName: burnerName,
            ArrivesOn: WeekStart,
            NoShift: Array.Empty<LocalDate>(),
            DietaryPreference: dietary,
            Allergies: allergies ?? Array.Empty<string>(),
            AllergyOtherText: allergyOther,
            Intolerances: Array.Empty<string>(),
            IntoleranceOtherText: null);

    private static WeeklyRosterDto Roster(params RosterPersonDto[] people) =>
        new(
            WeekStartOffset: 0,
            WeekStartDate: WeekStart,
            WeekEndDate: WeekStart.PlusDays(6),
            EventName: "Test Event",
            TotalUniqueOnSite: people.Length,
            UnansweredCount: 0,
            DietaryBreakdown: new Dictionary<string, int>(StringComparer.Ordinal),
            AllergyRollup: [],
            AllergyOtherEntries: [],
            IntoleranceRollup: [],
            IntoleranceOtherEntries: [],
            Days: [new DayRosterSummaryDto(0, WeekStart, people.Length, 0)],
            People: people,
            EventTodayDate: null);

    private static string[] RosterLines(WeeklyRosterDto roster)
    {
        var text = Encoding.UTF8.GetString(CantinaRosterCsvWriter.Write(roster));
        return text.TrimStart('﻿').TrimEnd('\r', '\n').Split("\r\n");
    }

    [HumansFact]
    public void Roster_KeepsSectionLayout_BomAndCrlf()
    {
        var bytes = CantinaRosterCsvWriter.Write(Roster(Person("Ana")));

        bytes[..3].Should().Equal(0xEF, 0xBB, 0xBF);

        var lines = RosterLines(Roster(Person("Ana")));
        lines[0].Should().Be("Week of 2026-07-07 – 2026-07-13");
        lines[1].Should().Be("Date,On site,Unanswered");
        lines[2].Should().Be("2026-07-07,1,0");
        lines[3].Should().BeEmpty(); // blank separator between sections
        lines[4].Should().Be("Name,ArrivesOn,NoShift,Dietary,Allergies,AllergyOther,Intolerances,IntoleranceOther");
        lines[5].Should().StartWith("Ana,2026-07-07");
    }

    [HumansFact]
    public void Roster_QuotesMultiValueCells_AndEscapesFormulaPrefixedNames()
    {
        var lines = RosterLines(Roster(
            Person("=SUM(A1)", allergies: ["Peanut", "Soy"], allergyOther: "@lookup")));

        var personRow = lines[5];
        personRow.Should().NotStartWith("="); // OWASP escape on user-controlled BurnerName
        personRow.Should().Contain("'=SUM(A1)");
        personRow.Should().Contain("\"Peanut, Soy\""); // joined multi-select stays one quoted cell
        personRow.Should().Contain("'@lookup");
    }

    [HumansFact]
    public void DailyMatrix_WritesHeaderPeopleAndTotals()
    {
        var dto = new DailyMatrixDto(
            DayOffset: 0,
            CalendarDate: WeekStart,
            EventTodayDate: null,
            EventName: "Test Event",
            WeekStartOffset: 0,
            TotalOnSite: 1,
            UnansweredCount: 0,
            DietaryBreakdown: new Dictionary<string, int>(StringComparer.Ordinal),
            AllergyRollup: [],
            AllergyOtherEntries: [],
            IntoleranceRollup: [],
            IntoleranceOtherEntries: [],
            People:
            [
                new DailyPersonRowDto(
                    Guid.NewGuid(), "+Ana", "Vegan",
                    new HashSet<string>(StringComparer.Ordinal), null,
                    new HashSet<string>(StringComparer.Ordinal), null),
            ]);

        var text = Encoding.UTF8.GetString(CantinaDailyMatrixCsvWriter.Write(dto)).TrimStart('﻿');
        var lines = text.TrimEnd('\r', '\n').Split("\r\n");

        lines[0].Should().Be("Cantina — 2026-07-07");
        lines[1].Should().Be("Total on site: 1");
        lines[2].Should().Be("Unanswered: 0");
        lines[3].Should().BeEmpty();
        lines[4].Should().StartWith("Burner,");
        lines[5].Should().StartWith("\"'+Ana\""); // injection escape on user-controlled name (escaped fields are also quoted)
        lines[^1].Should().StartWith("TOTALS,");
    }
}
