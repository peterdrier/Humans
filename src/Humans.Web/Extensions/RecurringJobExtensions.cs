using System.Linq.Expressions;
using System.Reflection;
using Hangfire;
using Hangfire.Storage;
using Humans.Base.Interfaces;

namespace Humans.Web.Extensions;

public static class RecurringJobExtensions
{
    /// <summary>
    /// One entry in the contributed set: the Hangfire job id, the type Hangfire resolves from
    /// DI when the job fires, the cron it runs on (empty means "configured off"), and the call
    /// that writes the schedule.
    /// </summary>
    internal sealed record ScheduledJob(string Id, Type JobType, string Cron, Action Schedule);

    public static void UseHumansRecurringJobs(this WebApplication app)
    {
        var logger = app.Services.GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(RecurringJobExtensions));

        var jobs = ContributedJobs(app.Services).ToList();

        var allScheduled = true;

        foreach (var job in jobs)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(job.Cron))
                {
                    // Opt-in job with no schedule configured. Drop any entry left from when
                    // it was switched on, so clearing the setting really stops the job.
                    RecurringJob.RemoveIfExists(job.Id);
                }
                else
                {
                    job.Schedule();
                }
            }
            catch (Exception ex)
            {
                // Don't let a stale distributed lock prevent the app from starting.
                allScheduled = false;
                logger.LogWarning(ex, "Failed to register recurring job '{JobId}' — will retry on next restart", job.Id);
            }
        }

        // Must run after the loop above: renaming a job id means writing the new entry
        // first and then sweeping the old one away.
        //
        // Only sweep when every job was written. If one failed, its old entry is still the
        // only working copy of that schedule, and we can't tell which stored id belongs to
        // the job that failed — removing it would stop the job until a later restart
        // happens to succeed. Skipping a sweep just leaves a dead entry around one more
        // boot, which is harmless.
        if (allScheduled)
        {
            RemoveJobsMissingFromContributedSet(jobs, logger);
        }
        else
        {
            logger.LogWarning(
                "Skipped sweeping unknown recurring jobs because at least one schedule failed to register");
        }
    }

    /// <summary>
    /// The jobs sections contribute through <see cref="ISectionJobs"/> — the single source of
    /// truth for scheduling and the stale-job sweep alike. Internal rather than private so
    /// <c>Humans.Integration.Tests</c> can walk the same set a real boot schedules.
    /// </summary>
    internal static IEnumerable<ScheduledJob> ContributedJobs(IServiceProvider services) =>
        SectionDiscoveryExtensions.DiscoverImplementations<ISectionJobs>()
            .SelectMany(contributor => contributor.Jobs(services))
            .Select(ToScheduledJob);

    /// <summary>
    /// Turns a descriptor into the same <c>RecurringJob.AddOrUpdate&lt;TJob&gt;</c> call the
    /// roll-call makes. Resolving the job type is deferred into the schedule action so a
    /// malformed descriptor fails inside the per-job try/catch rather than while the list is
    /// being built, where it would stop the app booting.
    /// </summary>
    internal static ScheduledJob ToScheduledJob(RecurringJobDescriptor descriptor) =>
        new(descriptor.Id, descriptor.JobType, descriptor.Cron, () => ScheduleContributed(descriptor));

    /// <summary>
    /// The generic argument is the job type, which Shell only knows at runtime — hence the one
    /// reflection hop onto <see cref="ScheduleTyped{TJob}"/>, whose body is the ordinary
    /// compiled call.
    /// </summary>
    private static void ScheduleContributed(RecurringJobDescriptor descriptor)
    {
        var execute = descriptor.JobType.GetMethod(nameof(IRecurringJob.ExecuteAsync), [typeof(CancellationToken)])
            ?? throw new InvalidOperationException(
                $"Job '{descriptor.Id}' names {descriptor.JobType.FullName}, which has no ExecuteAsync(CancellationToken)");

        typeof(RecurringJobExtensions)
            .GetMethod(nameof(ScheduleTyped), BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(descriptor.JobType)
            .Invoke(null, [descriptor.Id, execute, descriptor.Cron]);
    }

    /// <summary>
    /// The scheduling call, reached generically. <typeparamref name="TJob"/> may be an
    /// interface — Teams schedules against <c>ISystemTeamSync</c> — so the entry point is
    /// found by signature rather than through <see cref="IRecurringJob"/>.
    /// </summary>
    private static void ScheduleTyped<TJob>(string id, MethodInfo execute, string cron) where TJob : class =>
        RecurringJob.AddOrUpdate(id, BuildCall<TJob>(execute), cron);

    /// <summary>
    /// <c>job =&gt; job.ExecuteAsync(CancellationToken.None)</c>, built rather than written.
    /// A job whose <c>ExecuteAsync</c> returns <c>Task&lt;T&gt;</c> — Teams' sync returns a
    /// report — still types as <c>Func&lt;TJob, Task&gt;</c>, exactly as a hand-written lambda
    /// would.
    /// </summary>
    internal static Expression<Func<TJob, Task>> BuildCall<TJob>(MethodInfo execute) where TJob : class
    {
        var job = Expression.Parameter(typeof(TJob), "job");
        return Expression.Lambda<Func<TJob, Task>>(
            Expression.Call(job, execute, Expression.Constant(CancellationToken.None)),
            job);
    }

    /// <summary>
    /// Deletes stored schedules for jobs the app no longer has. A job that was deleted or
    /// renamed leaves its old Hangfire row behind, and that row names a type that no longer
    /// exists — so it throws on every tick until someone notices. Best-effort: a failure
    /// here must not stop the app from starting.
    /// </summary>
    private static void RemoveJobsMissingFromContributedSet(IReadOnlyList<ScheduledJob> jobs, ILogger logger)
    {
        try
        {
            // Opt-in jobs stay in the contributed set even when their schedule is switched
            // off, so turning one off never gets it swept away here.
            var known = jobs.Select(job => job.Id).ToHashSet(StringComparer.Ordinal);

            using var connection = JobStorage.Current.GetConnection();
            foreach (var stored in connection.GetRecurringJobs().Where(stored => !known.Contains(stored.Id)))
            {
                RecurringJob.RemoveIfExists(stored.Id);
                logger.LogInformation(
                    "Removed recurring job '{JobId}' — the app no longer has a job by that name", stored.Id);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to remove recurring jobs the app no longer has");
        }
    }
}
