using System.Globalization;
using ClosedXML.Excel;
using Humans.Application.DTOs.VolunteerTrackingExport;

namespace Humans.Web.Models.VolunteerTracking;

public sealed record VolunteerTrackingXlsxResult(byte[] Content, string ContentType, string FileName);

public sealed class VolunteerTrackingXlsxBuilder
{
    private const string XlsxContentType =
        "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    public VolunteerTrackingXlsxResult Build(VolunteerExportModel model)
    {
        using var workbook = new XLWorkbook();
        var sheet = workbook.Worksheets.Add("Volunteers");

        WriteMetadataBlock(sheet, model);
        // Day headers + body come in later tasks.

        using var stream = new MemoryStream();
        workbook.SaveAs(stream);
        return new VolunteerTrackingXlsxResult(stream.ToArray(), XlsxContentType, model.SuggestedFileName);
    }

    private static void WriteMetadataBlock(IXLWorksheet sheet, VolunteerExportModel model)
    {
        var generatedAt = model.GeneratedAtUtc.ToString("uuuu-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);
        sheet.Cell("A1").Value = $"Volunteer tracking export — generated {generatedAt} by {model.GeneratedByName}";
        sheet.Cell("A2").Value = model.FilterSummary;
        sheet.Cell("A3").Value = model.MethodologyBlurb;
        sheet.Cell("A3").Style.Alignment.WrapText = true;
        sheet.Cell("A3").Style.Font.Italic = true;
    }
}
