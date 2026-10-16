using System.Globalization;
using CsvHelper.Configuration;
using Humans.Base.Csv;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using AwesomeAssertions;
using Humans.Events.Services;
using NodaTime;
using Xunit;

namespace Humans.Events.Tests.Services;

public sealed class BulkEventCsvParserTests
{
    private readonly IStringLocalizer<EventsResource> _localizer =
        new StringLocalizer<EventsResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));

    private const string Header =
        "Id,Barrio,Status,Title,Description,Category,Date,StartTime,DurationMinutes,LocationNote,Host,IsRecurring,RecurrenceDays,PriorityRank";

    [HumansFact]
    public void Parse_SkipsCommentsBlankLinesAndHeader()
    {
        var csv = $"# a comment\n\n{Header}\n,Camp,,Title,Desc,Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var rows = BulkEventCsvParser.Parse(csv).Rows;

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Title");
        rows[0].Id.Should().BeNull();
        rows[0].RowNumber.Should().Be(4); // physical file row — what the user sees in Excel
    }

    [HumansFact]
    public void Parse_BlankPriorityRank_IsUnranked()
    {
        var csv = $"{Header}\n,Camp,,Title,Desc,Workshop,2026-07-08,09:30,60,,,false,,\n";

        var rows = BulkEventCsvParser.Parse(csv).Rows;

        rows.Should().ContainSingle();
        rows[0].PriorityRank.Should().BeNull();
    }

    [HumansFact]
    public void Parse_NonNumericPriorityRank_IsAnError()
    {
        var csv = $"{Header}\n,Camp,,Title,Desc,Workshop,2026-07-08,09:30,60,,,false,,high\n";

        var result = BulkEventCsvParser.Parse(csv);

        result.Rows.Should().BeEmpty();
        var error = result.Errors.Should().ContainSingle().Which;
        error.Key.Should().Be("Events_Upload_RowPriorityNotInteger");
        error.Args.Should().Equal(2);
    }

    [HumansFact]
    public void Parse_InvalidRecurringFlag_IsAnError()
    {
        var csv = $"{Header}\n,Camp,,Title,Desc,Workshop,2026-07-08,09:30,60,,,sometimes,,1\n";

        var result = BulkEventCsvParser.Parse(csv);

        result.Rows.Should().BeEmpty();
        var error = result.Errors.Should().ContainSingle().Which;
        error.Key.Should().Be("Events_Upload_RowRecurringBoolean");
        error.Args.Should().Equal(2);
    }

    [HumansFact]
    public void Parse_BlankRecurringFlag_IsNotRecurring()
    {
        var csv = $"{Header}\n,Camp,,Title,Desc,Workshop,2026-07-08,09:30,60,,,,,1\n";

        var rows = BulkEventCsvParser.Parse(csv).Rows;

        rows.Should().ContainSingle();
        rows[0].IsRecurring.Should().BeFalse();
    }

    [HumansFact]
    public void Parse_ColumnsInAnyOrder_MatchedByHeaderName()
    {
        var csv = "Title,Category,Date,StartTime,DurationMinutes,IsRecurring,PriorityRank,Description\n" +
                  "Yoga,Workshop,2026-07-08,09:30,60,false,1,Morning stretch\n";

        var rows = BulkEventCsvParser.Parse(csv).Rows;

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Yoga");
        rows[0].Description.Should().Be("Morning stretch");
        rows[0].DurationMinutes.Should().Be(60);
    }

    [HumansTheory]
    [InlineData(",", false)]
    [InlineData(";", false)]
    [InlineData(",", true)]
    [InlineData(";", true)]
    public void Parse_QuotedExtraHeader_DoesNotChangeDelimiter(string delimiter, bool comments)
    {
        var other = string.Equals(delimiter, ",", StringComparison.Ordinal) ? ';' : ',';
        var bytes = HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow(nameof(BulkEventCsvRecord.Title), nameof(BulkEventCsvRecord.Category), nameof(BulkEventCsvRecord.Date),
                nameof(BulkEventCsvRecord.StartTime), nameof(BulkEventCsvRecord.DurationMinutes), nameof(BulkEventCsvRecord.IsRecurring),
                nameof(BulkEventCsvRecord.PriorityRank), nameof(BulkEventCsvRecord.Description), $"Notes \"quoted\" {new string(other, 25)}");
            csv.WriteRow("Yoga", "Workshop", "2026-07-08", "09:30", "60", "false", "1", "Description", "working note");
        }, config =>
        {
            config.Delimiter = delimiter;
            config.InjectionOptions = InjectionOptions.None;
        });
        using var stream = new MemoryStream(bytes);
        using var reader = new StreamReader(stream);
        var text = (comments ? "# Comma-rich notes, for, the, template, and, extra, columns\n\n" : "") + reader.ReadToEnd();
        var rows = BulkEventCsvParser.Parse(text).Rows;
        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Yoga");
        rows[0].Description.Should().Be("Description");
        rows[0].DurationMinutes.Should().Be(60);
        rows[0].PriorityRank.Should().Be(1);
        rows[0].RowNumber.Should().Be(comments ? 4 : 2);
    }

    [HumansFact]
    public void Parse_ExtraColumns_AreIgnored()
    {
        var csv = $"{Header},My Notes\n,Camp,,Title,Desc,Workshop,2026-07-08,09:30,60,,,false,,1,bring speakers\n";

        var rows = BulkEventCsvParser.Parse(csv).Rows;

        rows.Should().ContainSingle();
        rows[0].PriorityRank.Should().Be(1);
    }

    [HumansFact]
    public void Parse_HeaderMatch_IsCaseAndWhitespaceForgiving()
    {
        var csv = "title, CATEGORY ,Date,StartTime,DurationMinutes,IsRecurring,PriorityRank,Description\n" +
                  "Yoga,Workshop,2026-07-08,09:30,60,false,1,Desc\n";

        var rows = BulkEventCsvParser.Parse(csv).Rows;

        rows.Should().ContainSingle();
        rows[0].Category.Should().Be("Workshop");
    }

    [HumansFact]
    public void Parse_SemicolonDelimited_SpanishExcel_IsDetected()
    {
        var csv = "Title;Category;Date;StartTime;DurationMinutes;IsRecurring;PriorityRank;Description\n" +
                  "Yoga;Workshop;2026-07-08;09:30;60;false;1;Desc\n";

        var rows = BulkEventCsvParser.Parse(csv).Rows;

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

        var rows = BulkEventCsvParser.Parse(csv).Rows;

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Yoga");
    }

    [HumansFact]
    public void Parse_QuotedMultilineDescription_RoundTrips()
    {
        var csv = $"{Header}\n,Camp,,Title,\"Line one\nLine two\",Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var rows = BulkEventCsvParser.Parse(csv).Rows;

        rows.Should().ContainSingle();
        rows[0].Description.Should().Be("Line one\nLine two");
    }

    [HumansFact]
    public void Parse_MissingRequiredColumn_ReturnsMissingColumnKey()
    {
        var csv = "Title,Category,Date,StartTime,DurationMinutes,IsRecurring,Description\n" +
                  "Yoga,Workshop,2026-07-08,09:30,60,false,Desc\n";

        var result = BulkEventCsvParser.Parse(csv);

        result.Rows.Should().BeEmpty();
        var error = result.Errors.Should().ContainSingle().Which;
        error.Key.Should().Be("Events_Upload_MissingRequiredColumns");
        error.Args.Should().Equal("PriorityRank");
    }

    [HumansFact]
    public void Parse_MultipleBadRows_AllErrorsReported()
    {
        var csv = $"{Header}\n" +
                  ",Camp,,Title,Desc,Workshop,2026-07-08,09:30,sixty,,,false,,1\n" +
                  "not-a-guid,Camp,,Other,Desc,Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var result = BulkEventCsvParser.Parse(csv);

        result.Rows.Should().BeEmpty();
        result.Errors.Select(error => error.Key).Should().Equal(
            "Events_Upload_RowDurationNotInteger", "Events_Upload_RowIdInvalidGuid");
        result.Errors[0].Args.Should().Equal(2);
        result.Errors[1].Args.Should().Equal(3);
    }

    [HumansFact]
    public void Parse_QuotedFieldWithComma_IsOneField()
    {
        var csv = $"{Header}\n,Camp,,\"Hello, World\",Desc,Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var rows = BulkEventCsvParser.Parse(csv).Rows;

        rows.Should().ContainSingle();
        rows[0].Title.Should().Be("Hello, World");
    }

    [HumansFact]
    public void Parse_EscapedQuotes_AreUnescaped()
    {
        var csv = $"{Header}\n,Camp,,Title,\"She said \"\"hi\"\"\",Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var rows = BulkEventCsvParser.Parse(csv).Rows;

        rows[0].Description.Should().Be("She said \"hi\"");
    }

    [HumansFact]
    public void Parse_RaggedRow_MissingCellsFailValueValidation()
    {
        // Missing trailing cells read as empty (Excel often drops them) — the
        // required-value checks surface the real problem instead of a column count.
        var csv = $"{Header}\n,Camp,,Title\n";

        var result = BulkEventCsvParser.Parse(csv);

        result.Rows.Should().BeEmpty();
        var error = result.Errors.Should().ContainSingle().Which;
        error.Key.Should().Be("Events_Upload_RowDurationNotInteger");
        error.Args.Should().Equal(2);
    }

    [HumansFact]
    public void Parse_InvalidId_ReturnsKey()
    {
        var csv = $"{Header}\nnot-a-guid,Camp,,Title,Desc,Workshop,2026-07-08,09:30,60,,,false,,1\n";

        var result = BulkEventCsvParser.Parse(csv);

        result.Rows.Should().BeEmpty();
        var error = result.Errors.Should().ContainSingle().Which;
        error.Key.Should().Be("Events_Upload_RowIdInvalidGuid");
        error.Args.Should().Equal(2);
    }

    [HumansTheory]
    [InlineData("en", "Row 2: DurationMinutes is not an integer.")]
    [InlineData("es", "Fila 2: DurationMinutes no es un número entero.")]
    [InlineData("de", "Zeile 2: DurationMinutes ist keine ganze Zahl.")]
    [InlineData("it", "Riga 2: DurationMinutes non è un numero intero.")]
    [InlineData("fr", "Ligne 2 : DurationMinutes n’est pas un nombre entier.")]
    [InlineData("ca", "Fila 2: DurationMinutes no és un nombre enter.")]
    public void Parse_NonIntegerDuration_KeyRendersInUploaderCultureWithFileRow(string culture, string expectedError)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
            var csv = $"{Header}\n,Camp,,Title,Desc,Workshop,2026-07-08,09:30,sixty,,,false,,1\n";

            var result = BulkEventCsvParser.Parse(csv);
            var error = result.Errors.Should().ContainSingle().Which;
            error.Key.Should().Be("Events_Upload_RowDurationNotInteger");
            error.Args.Should().Equal(2);
            _localizer[error.Key, error.Args].Value.Should().Be(expectedError);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
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
    [InlineData("Mon Wed Fri", true)]
    [InlineData("Mon Funday", false)]
    public void HasOnlyDisplayDays_RecognizesTheCsvDayVocabulary(string days, bool expected)
    {
        EventRecurrenceDays.HasOnlyDisplayDays(days).Should().Be(expected);
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
