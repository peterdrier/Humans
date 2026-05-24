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
        WriteDayHeaders(sheet, model);
        sheet.SheetView.FreezeRows(6);
        sheet.SheetView.FreezeColumns(1);
        // Body comes in later tasks.

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

    private static void WriteDayHeaders(IXLWorksheet sheet, VolunteerExportModel model)
    {
        for (var i = 0; i < model.Days.Count; i++)
        {
            var col = i + 2;  // start at column B
            var d = model.Days[i];
            sheet.Cell(5, col).Value = d.DayOfWeek.ToString().Substring(0, 3); // Mon, Tue, ...
            sheet.Cell(6, col).Value = $"{d.Day:D2}/{d.Month:D2}/{d.Year:D4}";
            sheet.Cell(5, col).Style.Font.Bold = true;
            sheet.Cell(6, col).Style.Font.Bold = true;
        }
    }
}
