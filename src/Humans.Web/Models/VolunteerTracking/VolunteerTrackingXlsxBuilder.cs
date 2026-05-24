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

        if (model.Groups.Count == 0)
        {
            sheet.Cell(7, 1).Value = "No confirmed humans in this range.";
            sheet.Cell(7, 1).Style.Font.Italic = true;
        }
        else
        {
            var nextRow = WriteGroupsAndHumans(sheet, model, startRow: 7);
            WriteTotalsRow(sheet, model, totalsRow: nextRow);
        }

        sheet.Columns().AdjustToContents();

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
            var bannerRange = sheet.Range(row, 1, row, lastCol);
            sheet.Cell(row, 1).Value = $"{group.TeamName} ({group.Humans.Count} humans)";
            bannerRange.Merge();
            bannerRange.Style.Fill.BackgroundColor = XLColor.FromHtml(group.TeamColorHex);
            bannerRange.Style.Font.Bold = true;
            bannerRange.Style.Font.FontColor = XLColor.White;
            row++;

            // Human rows
            foreach (var human in group.Humans)
            {
                sheet.Cell(row, 1).Value = human.PlayaName;
                for (var i = 0; i < human.Cells.Count; i++)
                {
                    var cell = sheet.Cell(row, i + 2);
                    var state = human.Cells[i];
                    switch (state.Kind)
                    {
                        case CellKind.Empty:
                            // no value, no fill
                            break;
                        case CellKind.Arrival:
                            cell.Value = human.PlayaName;
                            cell.Style.Fill.BackgroundColor = XLColor.White;
                            cell.Style.Border.OutsideBorder = XLBorderStyleValues.Thin;
                            break;
                        case CellKind.Worked:
                            cell.Value = human.PlayaName;
                            cell.Style.Fill.BackgroundColor = XLColor.FromHtml(state.TeamColorHex!);
                            cell.Style.Font.FontColor = XLColor.White;
                            cell.Style.Font.Bold = true;
                            break;
                        default:
                            break;
                    }
                }
                row++;
            }
        }
        return row;
    }

    private static void WriteTotalsRow(IXLWorksheet sheet, VolunteerExportModel model, int totalsRow)
    {
        sheet.Cell(totalsRow, 1).Value = "Total humans on-site";
        sheet.Cell(totalsRow, 1).Style.Font.Bold = true;
        for (var i = 0; i < model.TotalsPerDay.Count; i++)
        {
            var cell = sheet.Cell(totalsRow, i + 2);
            cell.Value = model.TotalsPerDay[i];
            cell.Style.Font.Bold = true;
        }
    }
}
