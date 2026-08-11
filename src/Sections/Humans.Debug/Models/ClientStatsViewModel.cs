namespace Humans.Debug.Models;

/// <summary>
/// View model for the <c>/Debug/ClientStats</c> screen: coarse client
/// demographics (since process start) plus the HTTP status-code tally.
/// </summary>
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

/// <summary>One labelled count with its share of the relevant total.</summary>
internal sealed record ClientStatRow(string Label, long Count, double Percent);

/// <summary>One HTTP status code with its category and share of all responses.</summary>
internal sealed record HttpStatusRow(int StatusCode, string Category, long Count, double Percent);

/// <summary>Render model for the reusable <c>_ClientStatTable</c> partial (one card).</summary>
internal sealed record ClientStatTableModel(
    string Title,
    string Icon,
    long Total,
    IReadOnlyList<ClientStatRow> Rows);
