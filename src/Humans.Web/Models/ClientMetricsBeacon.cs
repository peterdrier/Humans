namespace Humans.Web.Models;

/// <summary>
/// Beacon payload posted by <c>/js/client-metrics.js</c> (screen dimensions). Shell's, not
/// Debug's: <c>Program.cs</c> binds it on the <c>/api/client-metrics</c> minimal-API endpoint
/// and forwards it to <c>IClientStatsTracker</c>. It stayed behind when the rest of
/// <c>ClientStatsViewModel.cs</c> moved into <c>Humans.Debug</c> at G5
/// (nobodies-collective/Humans#866) — the Debug pages read the tracker, they do not feed it.
/// </summary>
public sealed record ClientMetricsBeacon(int ScreenWidth, int ScreenHeight);
