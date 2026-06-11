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
        rows[0].RowNumber.Should().Be(4); // physical file row — what the user sees in Excel
    }

    [HumansFact]
    public void Parse_ColumnsInAnyOrder_MatchedByHeaderName()
    {
        var csv = "Title,Category,Date,StartTime,DurationMinutes,IsRecurring,PriorityRank,Description\n" +
                  "Yoga,Workshop,2026-07-08,09:30,60,false,1,Morning stretch\n";

        var rows = BulkEventCsvParser.Parse(csv);

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Yoga");
        rows[0].Description.Should().Be("Morning stretch");
        rows[0].DurationMinutes.Should().Be(60);
    }

    [HumansFact]
    public void Parse_ExtraColumns_AreIgnored()
    {
        var csv = $"{Header},My Notes\n,Camp,,Title,Desc,Workshop,2026-07-08,09:30,60,,,false,,1,bring speakers\n";

        var rows = BulkEventCsvParser.Parse(csv);

        rows.Should().ContainSingle();
        rows[0].PriorityRank.Should().Be(1);
    }

    [HumansFact]
    public void Parse_HeaderMatch_IsCaseAndWhitespaceForgiving()
    {
        var csv = "title, CATEGORY ,Date,StartTime,DurationMinutes,IsRecurring,PriorityRank,Description\n" +
                  "Yoga,Workshop,2026-07-08,09:30,60,false,1,Desc\n";

        var rows = BulkEventCsvParser.Parse(csv);

        rows.Should().ContainSingle();
        rows[0].Category.Should().Be("Workshop");
    }

    [HumansFact]
    public void Parse_SemicolonDelimited_SpanishExcel_IsDetected()
    {
        var csv = "Title;Category;Date;StartTime;DurationMinutes;IsRecurring;PriorityRank;Description\n" +
                  "Yoga;Workshop;2026-07-08;09:30;60;false;1;Desc\n";

        var rows = BulkEventCsvParser.Parse(csv);

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Yoga");
        rows[0].DurationMinutes.Should().Be(60);
    }

    [HumansFact]
    public void Parse_SemicolonData_WithCommaRichCommentBanner_IsDetected()
    {
        // The realistic Spanish-Excel re-save: data rows become semicolon-delimited
        // but the template's comment banner (full of commas) survives verbatim.
        var csv = "# ELSEWHERE EVENT GUIDE — Bulk Upload Template\n" +
                  "# VALID CATEGORIES\n" +
                  "#   Workshop, Music, Performance, Food, Wellness, Kids\n" +
                  "#   Save as CSV (comma-separated, UTF-8) before uploading, please.\n" +
                  "Title;Category;Date;StartTime;DurationMinutes;IsRecurring;PriorityRank;Description\n" +
                  "Yoga;Workshop;2026-07-08;09:30;60;false;1;Desc\n";

        var rows = BulkEventCsvParser.Parse(csv);

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Yoga");
    }

    [HumansFact]
    public void Parse_QuotedMultilineDescription_RoundTrips()
    {
        var csv = $"{Header}\n,Camp,,Title,\"Line one\nLine two\",Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var rows = BulkEventCsvParser.Parse(csv);

        rows.Should().ContainSingle();
        rows[0].Description.Should().Be("Line one\nLine two");
    }

    [HumansFact]
    public void Parse_MissingRequiredColumn_ThrowsFriendlyError()
    {
        var csv = "Title,Category,Date,StartTime,DurationMinutes,IsRecurring,Description\n" +
                  "Yoga,Workshop,2026-07-08,09:30,60,false,Desc\n";

        var act = () => BulkEventCsvParser.Parse(csv);

        act.Should().Throw<FormatException>().WithMessage("*missing required column(s): PriorityRank*");
    }

    [HumansFact]
    public void Parse_MultipleBadRows_AllErrorsReported()
    {
        var csv = $"{Header}\n" +
                  ",Camp,,Title,Desc,Workshop,2026-07-08,09:30,sixty,,,false,,1\n" +
                  "not-a-guid,Camp,,Other,Desc,Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var act = () => BulkEventCsvParser.Parse(csv);

        act.Should().Throw<FormatException>()
            .Where(e => e.Message.Contains("Row 2: DurationMinutes is not an integer.")
                     && e.Message.Contains("Row 3: Id is not a valid Guid."));
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
    public void Parse_RaggedRow_MissingCellsFailValueValidation()
    {
        // Missing trailing cells read as empty (Excel often drops them) — the
        // required-value checks surface the real problem instead of a column count.
        var csv = $"{Header}\n,Camp,,Title\n";

        var act = () => BulkEventCsvParser.Parse(csv);

        act.Should().Throw<FormatException>().WithMessage("*DurationMinutes is not an integer*");
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
