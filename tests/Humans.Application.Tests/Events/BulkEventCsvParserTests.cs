using AwesomeAssertions;
using Humans.Application.Events;
using NodaTime;
using Xunit;

namespace Humans.Application.Tests.Services;

public sealed class BulkEventCsvParserTests
{
    private const string Header =
        "Id,Barrio,Status,Title,Description,Category,Date,StartTime,DurationMinutes,LocationNote,Host,IsRecurring,RecurrenceDays,PriorityRank";

    [HumansFact]
    public void Parse_SkipsCommentsBlankLinesAndHeader()
    {
        var csv = $"# a comment\n\n{Header}\n,Camp,,Title,Desc,Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var rows = BulkEventCsvParser.Parse(csv);

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Title");
        rows[0].Id.Should().BeNull();
    }

    [HumansFact]
    public void Parse_QuotedFieldWithComma_IsOneField()
    {
        var csv = $"{Header}\n,Camp,,\"Hello, World\",Desc,Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var rows = BulkEventCsvParser.Parse(csv);

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Hello, World");
    }

    [HumansFact]
    public void Parse_EscapedQuotes_AreUnescaped()
    {
        var csv = $"{Header}\n,Camp,,Title,\"She said \"\"hi\"\"\",Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var rows = BulkEventCsvParser.Parse(csv);

        rows[0].Description.Should().Be("She said \"hi\"");
    }

    [HumansFact]
    public void Parse_TooFewColumns_Throws()
    {
        var csv = $"{Header}\n,Camp,,Title\n";

        var act = () => BulkEventCsvParser.Parse(csv);

        act.Should().Throw<FormatException>().WithMessage("*expected 14 columns*");
    }

    [HumansFact]
    public void Parse_InvalidId_Throws()
    {
        var csv = $"{Header}\nnot-a-guid,Camp,,Title,Desc,Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var act = () => BulkEventCsvParser.Parse(csv);

        act.Should().Throw<FormatException>().WithMessage("*not a valid Guid*");
    }

    [HumansFact]
    public void Parse_NonIntegerDuration_Throws()
    {
        var csv = $"{Header}\n,Camp,,Title,Desc,Workshop,2026-07-08,09:30,sixty,,,false,,1\n";

        var act = () => BulkEventCsvParser.Parse(csv);

        act.Should().Throw<FormatException>().WithMessage("*DurationMinutes is not an integer*");
    }
}

public sealed class EventRecurrenceDaysTests
{
    [HumansFact]
    public void OffsetsToDisplayDays_MapsEachOffsetToItsWeekday()
    {
        var gate = new LocalDate(2026, 7, 6); // Monday

        EventRecurrenceDays.OffsetsToDisplayDays("0,2,4", gate).Should().Be("Mon Wed Fri");
    }

    [HumansFact]
    public void DisplayDaysToOffsets_RoundTripsWithinOneWeek()
    {
        var gate = new LocalDate(2026, 7, 6); // Monday

        EventRecurrenceDays.DisplayDaysToOffsets("Mon Wed Fri", gate, 6).Should().Be("0,2,4");
    }

    [HumansFact]
    public void DisplayDaysToOffsets_ReturnsNull_WhenNoDayMatches()
    {
        var gate = new LocalDate(2026, 7, 6); // Monday, window Mon..Sun

        EventRecurrenceDays.DisplayDaysToOffsets("Mon", gate, 0).Should().Be("0");
        EventRecurrenceDays.DisplayDaysToOffsets("Tue", gate, 0).Should().BeNull();
    }

    [HumansTheory]
    [InlineData("Mon", "Mon", true)]
    [InlineData("Mon Wed", "Wed Mon", true)]
    [InlineData("mon", "MON", true)]
    [InlineData("Mon", "Mon Wed", false)]
    public void SameDays_ComparesAsCaseInsensitiveSet(string a, string b, bool expected)
    {
        EventRecurrenceDays.SameDays(a, b).Should().Be(expected);
    }
}
