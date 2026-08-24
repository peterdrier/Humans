namespace Humans.Base.Interfaces;

/// <summary>
/// One recurring job a section owns: the Hangfire job id, the type Hangfire resolves from
/// DI when the job fires, and the cron it runs on.
/// </summary>
/// <remarks>
/// Ids are stored in Hangfire, so they must never change — a renamed id is a new job plus a
/// swept-away old one. Keep the section-first convention (<c>teams-system-sync</c>).
///
/// An empty <paramref name="Cron"/> means "configured off": the job keeps its place in the
/// contributed set (so the stale-job sweep does not remove it) but is not scheduled.
///
/// <paramref name="JobType"/> is the type Hangfire resolves, and may be an interface —
/// Teams schedules against <c>ISystemTeamSync</c>. Its entry point is
/// <c>ExecuteAsync(CancellationToken)</c>, which every job type carries whether or not it
/// implements <see cref="IRecurringJob"/>.
/// </remarks>
public sealed record RecurringJobDescriptor(string Id, Type JobType, string Cron);

/// <summary>The recurring jobs a section owns. Shell schedules and sweeps the merged set.</summary>
public interface ISectionJobs : ISectionContribution
{
    /// <summary>The section's recurring jobs, built at registration time.</summary>
    /// <param name="services">Root provider — resolve <c>IConfiguration</c> or
    /// <c>ConfigurationRegistry</c> from it for cron values that come from settings.</param>
    IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services);
}
