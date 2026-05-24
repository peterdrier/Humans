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

        var nextRow = WriteGroupsAndHumans(sheet, model, startRow: 7);
        WriteTotalsRow(sheet, model, totalsRow: nextRow);

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

    private static int WriteGroupsAndHumans(IXLWorksheet sheet, VolunteerExportModel model, int startRow)
    {
        var dayCount = model.Days.Count;
        var lastCol = dayCount + 1;  // 1 label + day columns
        var row = startRow;
        foreach (var group in model.Groups)
        {
            // Banner row
            var banner = sheet.Cell(row, 1);
            banner.Value = $"{group.TeamName} ({group.Humans.Count} humans)";
            var range = sheet.Range(row, 1, row, lastCol);
            range.Merge();
            range.Style.Fill.BackgroundColor = XLColor.FromHtml(group.TeamColorHex);
            range.Style.Font.Bold = true;
            range.Style.Font.FontColor = XLColor.White;
            row++;

            // Human rows come in Task 4.4 — for now, just advance the row counter to leave space.
            foreach (var _ in group.Humans) row++;
        }
        return row;
    }

    private static void WriteTotalsRow(IXLWorksheet sheet, VolunteerExportModel model, int totalsRow)
    {
        // Filled in Task 4.5.
        _ = sheet;
        _ = model;
        _ = totalsRow;
    }
}
