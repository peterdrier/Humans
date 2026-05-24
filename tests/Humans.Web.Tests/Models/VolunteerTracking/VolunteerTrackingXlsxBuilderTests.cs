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

    [HumansFact]
    public void DepartmentBanner_RenderedAsMergedColoredRow()
    {
        var days = new[] { new LocalDate(2026, 7, 7), new LocalDate(2026, 7, 8) };
        var teamA = Guid.Parse("11111111-0000-0000-0000-000000000000");
        var group = new DepartmentGroup(
            TeamId: teamA,
            TeamName: "Cantina",
            TeamColorHex: "#1F77B4",
            Humans:
            [
                new HumanRow(Guid.NewGuid(), "Alice", [CellState.Empty, CellState.Empty]),
            ]);
        var model = NewEmptyModel(days) with { Groups = [group], TotalsPerDay = [0, 0] };

        var sut = new VolunteerTrackingXlsxBuilder();
        using var workbook = new XLWorkbook(new MemoryStream(sut.Build(model).Content));
        var sheet = workbook.Worksheets.First();

        // Body starts at row 7. Banner is row 7. Humans below from row 8.
        var bannerCell = sheet.Cell("A7");
        bannerCell.GetString().Should().Be("Cantina (1 humans)");
        bannerCell.Style.Fill.BackgroundColor.ToString().Should().EndWith("1F77B4");
        bannerCell.Style.Font.Bold.Should().BeTrue();
        bannerCell.Style.Font.FontColor.ToString().Should().EndWith("FFFFFF");

        var merged = sheet.MergedRanges.FirstOrDefault(r => r.RangeAddress.FirstAddress.RowNumber == 7);
        merged.Should().NotBeNull();
        merged!.RangeAddress.LastAddress.ColumnNumber.Should().Be(3); // A..C (1 label + 2 day cols)
    }

    [HumansFact]
    public void HumanRow_RendersNameAndColoredCells()
    {
        var days = new[] { new LocalDate(2026, 7, 7), new LocalDate(2026, 7, 8), new LocalDate(2026, 7, 9) };
        var teamA = Guid.Parse("11111111-0000-0000-0000-000000000000");
        var aliceCells = new[]
        {
            CellState.Arrival,
            CellState.Worked(teamA, "#1F77B4"),
            CellState.Empty,
        };
        var group = new DepartmentGroup(
            TeamId: teamA,
            TeamName: "Cantina",
            TeamColorHex: "#1F77B4",
            Humans: [new HumanRow(Guid.NewGuid(), "Alice", aliceCells)]);
        var model = NewEmptyModel(days) with { Groups = [group], TotalsPerDay = [0, 1, 0] };

        var sut = new VolunteerTrackingXlsxBuilder();
        using var workbook = new XLWorkbook(new MemoryStream(sut.Build(model).Content));
        var sheet = workbook.Worksheets.First();

        // Row 7 banner, row 8 Alice.
        sheet.Cell("A8").GetString().Should().Be("Alice");
        // Day 1 (col B) = Arrival — white fill, name in cell.
        sheet.Cell("B8").GetString().Should().Be("Alice");
        sheet.Cell("B8").Style.Fill.BackgroundColor.ToString().Should().EndWith("FFFFFF");
        // Day 2 (col C) = Worked — team color fill, name.
        sheet.Cell("C8").GetString().Should().Be("Alice");
        sheet.Cell("C8").Style.Fill.BackgroundColor.ToString().Should().EndWith("1F77B4");
        // Day 3 (col D) = Empty — no fill, no text.
        sheet.Cell("D8").GetString().Should().BeEmpty();
        sheet.Cell("D8").Style.Fill.BackgroundColor.ToString().Should().NotEndWith("FFFFFF");
    }

    [HumansFact]
    public void TotalsRow_RendersUnderLastGroup_WithLabelAndPerDayCounts()
    {
        var days = new[] { new LocalDate(2026, 7, 7), new LocalDate(2026, 7, 8) };
        var teamA = Guid.Parse("11111111-0000-0000-0000-000000000000");
        var humans = new[]
        {
            new HumanRow(Guid.NewGuid(), "Alice", [CellState.Worked(teamA, "#1F77B4"), CellState.Empty]),
            new HumanRow(Guid.NewGuid(), "Bob",   [CellState.Worked(teamA, "#1F77B4"), CellState.Worked(teamA, "#1F77B4")]),
        };
        var group = new DepartmentGroup(teamA, "Cantina", "#1F77B4", humans);
        var model = NewEmptyModel(days) with { Groups = [group], TotalsPerDay = [2, 1] };

        var sut = new VolunteerTrackingXlsxBuilder();
        using var workbook = new XLWorkbook(new MemoryStream(sut.Build(model).Content));
        var sheet = workbook.Worksheets.First();

        // Row layout: 1-3 metadata, 4 blank, 5-6 day headers, 7 banner, 8 Alice, 9 Bob, 10 totals.
        sheet.Cell("A10").GetString().Should().Be("Total humans on-site");
        sheet.Cell("A10").Style.Font.Bold.Should().BeTrue();
        sheet.Cell("B10").GetDouble().Should().Be(2);
        sheet.Cell("C10").GetDouble().Should().Be(1);
        sheet.Cell("B10").Style.Font.Bold.Should().BeTrue();
    }

    [HumansFact]
    public void EmptyRoster_RendersHelpfulHintRow_AndColumnsAutoFit()
    {
        var days = new[] { new LocalDate(2026, 7, 7) };
        var model = NewEmptyModel(days);
        var sut = new VolunteerTrackingXlsxBuilder();
        using var workbook = new XLWorkbook(new MemoryStream(sut.Build(model).Content));
        var sheet = workbook.Worksheets.First();

        sheet.Cell("A7").GetString().Should().Be("No confirmed humans in this range.");
        sheet.Cell("B7").GetString().Should().BeEmpty();      // no stray totals row
        sheet.Cell("A8").GetString().Should().BeEmpty();      // no totals row at all when empty
        sheet.Column(1).Width.Should().BeGreaterThan(0);
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
