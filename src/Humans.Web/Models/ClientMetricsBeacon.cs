namespace Humans.Web.Models;

/// <summary>
/// Beacon payload from <c>/js/client-metrics.js</c>. Shell's, not Debug's: <c>Program.cs</c> binds it on
/// <c>/api/client-metrics</c> and feeds the tracker the Debug pages only read.
/// </summary>
public sealed record ClientMetricsBeacon(int ScreenWidth, int ScreenHeight);
