namespace Humans.Application.Interfaces.Admin;

/// <summary>
/// Composite system-health snapshot for the admin dashboard.
/// Aggregates recent error-level log events and Hangfire failed-job count
/// into a single at-a-glance health signal.
/// </summary>
public sealed record AdminSystemHealth(int ErrorsLast24h, int FailedJobs)
{
    public bool AllNormal => ErrorsLast24h == 0 && FailedJobs == 0;
}

/// <summary>
/// Provides a composite system-health snapshot for admin use.
/// Implementation lives in <c>Humans.Web</c> (which references Hangfire and
/// <c>InMemoryLogSink</c>); this interface keeps the contract in Application
/// so controllers can declare the parameter without a Web-layer dependency.
/// </summary>
public interface IAdminDashboardService
{
    Task<AdminSystemHealth> GetSystemHealthAsync(CancellationToken ct = default);
}
