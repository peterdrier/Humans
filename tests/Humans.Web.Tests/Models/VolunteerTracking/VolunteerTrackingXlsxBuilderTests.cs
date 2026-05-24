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
}
