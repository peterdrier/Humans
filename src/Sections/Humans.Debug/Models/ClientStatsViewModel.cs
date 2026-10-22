namespace Humans.Debug.Models;

/// <summary>Counts since process start.</summary>
internal sealed record ClientStatsViewModel(
    long TotalPageViews,
    IReadOnlyList<ClientStatRow> OperatingSystems,
    IReadOnlyList<ClientStatRow> Browsers,
    IReadOnlyList<ClientStatRow> DeviceTypes,
    long TotalBotPageViews,
    IReadOnlyList<ClientStatRow> Bots,
    long TotalResolutionSamples,
    IReadOnlyList<ClientStatRow> Resolutions,
    long TotalResponses,
    IReadOnlyList<HttpStatusRow> StatusCodes);

internal sealed record ClientStatRow(string Label, long Count, double Percent);

internal sealed record HttpStatusRow(int StatusCode, string Category, long Count, double Percent);

internal sealed record ClientStatTableModel(
    string Title,
    string Icon,
    long Total,
    IReadOnlyList<ClientStatRow> Rows);
