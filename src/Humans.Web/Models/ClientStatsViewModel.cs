namespace Humans.Web.Models;

/// <summary>
/// View model for the <c>/Admin/ClientStats</c> debug screen: coarse client
/// demographics (since process start) plus the HTTP status-code tally.
/// </summary>
public sealed record ClientStatsViewModel(
    long TotalPageViews,
    IReadOnlyList<ClientStatRow> OperatingSystems,
    IReadOnlyList<ClientStatRow> Browsers,
    IReadOnlyList<ClientStatRow> DeviceTypes,
    long TotalResolutionSamples,
    IReadOnlyList<ClientStatRow> Resolutions,
    long TotalResponses,
    IReadOnlyList<HttpStatusRow> StatusCodes);

/// <summary>One labelled count with its share of the relevant total.</summary>
public sealed record ClientStatRow(string Label, long Count, double Percent);

/// <summary>One HTTP status code with its category and share of all responses.</summary>
public sealed record HttpStatusRow(int StatusCode, string Category, long Count, double Percent);

/// <summary>Beacon payload posted by <c>/js/client-metrics.js</c> (screen dimensions).</summary>
public sealed record ClientMetricsBeacon(int ScreenWidth, int ScreenHeight);

/// <summary>Render model for the reusable <c>_ClientStatTable</c> partial (one card).</summary>
public sealed record ClientStatTableModel(
    string Title,
    string Icon,
    long Total,
    IReadOnlyList<ClientStatRow> Rows);
