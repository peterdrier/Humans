using Hangfire;
using Humans.Base.Interfaces;

namespace Humans.Web.Extensions;

/// <summary>
/// Unschedules the recurring jobs of sections switched off for the event, so they do not email members about pages
/// that answer 404 or sync with services the event does not use, and schedules them again when the section is switched
/// back on. A job marked <see cref="Humans.Base.Attributes.StaysOnWhenSectionIsOffAttribute"/> keeps running.
/// </summary>
/// <remarks>
/// Polls rather than listening for settings writes: the switched-off set is held in memory, so a read is free, and a
/// switch takes effect within <see cref="Interval"/>. The jobs stay in the contributed set, so the boot-time stale-job
/// sweep never removes them.
/// </remarks>
internal sealed class SwitchedOffSectionJobs(
    IServiceProvider services,
    ISectionSwitches switches,
    IRecurringJobManager manager,
    ILogger<SwitchedOffSectionJobs> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    // The boot pass in UseHumansRecurringJobs schedules every job, as if nothing were switched off.
    private IReadOnlySet<string> _applied = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await ApplyAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
            await ApplyAsync(stoppingToken);
    }

    private async Task ApplyAsync(CancellationToken cancellationToken)
    {
        try
        {
            var switchedOff = await switches.GetSwitchedOffAsync(cancellationToken);
            if (switchedOff.SetEquals(_applied))
                return;

            foreach (var job in Changed(RecurringJobExtensions.ContributedJobs(services), _applied, switchedOff))
            {
                if (job.SwitchedOff)
                {
                    manager.RemoveIfExists(job.Job.Id);
                    logger.LogInformation("Paused recurring job {JobId}: its section is switched off for the event", job.Job.Id);
                }
                else
                {
                    job.Job.Schedule(manager);
                    logger.LogInformation("Resumed recurring job {JobId}: its section is switched on again", job.Job.Id);
                }
            }

            _applied = switchedOff;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Left as it was; the next tick tries again.
            logger.LogError(ex, "Failed to pause or resume the recurring jobs of switched-off sections");
        }
    }

    /// <summary>
    /// The scheduled jobs whose section changed between the applied set and the current one, with whether each is now
    /// off. A job configured off (empty cron) stays off either way.
    /// </summary>
    internal static IEnumerable<(RecurringJobExtensions.ScheduledJob Job, bool SwitchedOff)> Changed(
        IEnumerable<RecurringJobExtensions.ScheduledJob> jobs, IReadOnlySet<string> applied, IReadOnlySet<string> switchedOff) =>
        jobs.Where(job => !string.IsNullOrWhiteSpace(job.Cron))
            .Select(job => (Job: job, Section: SectionSwitchboard.SectionNameOf(job.JobType)))
            .Where(job => applied.Contains(job.Section) != switchedOff.Contains(job.Section))
            .Select(job => (job.Job, switchedOff.Contains(job.Section)));
}
