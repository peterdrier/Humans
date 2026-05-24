using AwesomeAssertions;
using ClosedXML.Excel;
using Humans.Application.DTOs.VolunteerTrackingExport;
using Humans.Web.Models.VolunteerTracking;
using NodaTime;

namespace Humans.Web.Tests.Models.VolunteerTracking;

public sealed class VolunteerTrackingXlsxBuilderTests
{
    private static readonly Instant TestNow = Instant.FromUtc(2026, 5, 23, 12, 0);

    [HumansFact]
    public void EmptyModel_ProducesValidXlsxWithMetadataBlock()
    {
        var model = new VolunteerExportModel(
            MethodologyBlurb: "Methodology text.",
            FilterSummary: "Department: All · Range: 2026-07-07 → 2026-07-13 (custom)",
            GeneratedAtUtc: TestNow,
            GeneratedByName: "TestActor",
            Days: [new LocalDate(2026, 7, 7)],
            Groups: [],
            TotalsPerDay: [0],
            SuggestedFileName: "volunteer-tracking-2026-07-07-to-2026-07-07.xlsx");

        var sut = new VolunteerTrackingXlsxBuilder();
        var result = sut.Build(model);

        result.ContentType.Should().Be("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");
        result.FileName.Should().Be(model.SuggestedFileName);
        result.Content.Should().NotBeEmpty();

        using var stream = new MemoryStream(result.Content);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();

        sheet.Name.Should().Be("Volunteers");
        sheet.Cell("A1").GetString().Should().Contain("Volunteer tracking export").And.Contain("TestActor");
        sheet.Cell("A2").GetString().Should().Be("Department: All · Range: 2026-07-07 → 2026-07-13 (custom)");
        sheet.Cell("A3").GetString().Should().Be("Methodology text.");
    }

    [HumansFact]
    public void DayHeaders_RenderDayOfWeekAndDate_InRows5And6()
    {
        var days = new[]
        {
            new LocalDate(2026, 7, 7),  // Tue
            new LocalDate(2026, 7, 8),  // Wed
            new LocalDate(2026, 7, 9),  // Thu
        };
        var model = NewEmptyModel(days);
        var sut = new VolunteerTrackingXlsxBuilder();
        using var workbook = new XLWorkbook(new MemoryStream(sut.Build(model).Content));
        var sheet = workbook.Worksheets.First();

        // Column A reserved; day columns start at B.
        sheet.Cell("B5").GetString().Should().Be("Tue");
        sheet.Cell("C5").GetString().Should().Be("Wed");
        sheet.Cell("D5").GetString().Should().Be("Thu");
        sheet.Cell("B6").GetString().Should().Be("07/07/2026");
        sheet.Cell("C6").GetString().Should().Be("08/07/2026");
        sheet.Cell("D6").GetString().Should().Be("09/07/2026");

        sheet.SheetView.SplitRow.Should().Be(6);
        sheet.SheetView.SplitColumn.Should().Be(1);
    }

    private static VolunteerExportModel NewEmptyModel(IReadOnlyList<LocalDate> days) => new(
        MethodologyBlurb: "M.",
        FilterSummary: "F.",
        GeneratedAtUtc: TestNow,
        GeneratedByName: "Tester",
        Days: days,
        Groups: [],
        TotalsPerDay: Enumerable.Repeat(0, days.Count).ToArray(),
        SuggestedFileName: "x.xlsx");
}
