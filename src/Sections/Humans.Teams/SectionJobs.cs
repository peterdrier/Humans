using Humans.Base.Interfaces;
using Humans.Teams.Contracts;

namespace Humans.Teams;

/// <summary>Teams' recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // The system-team sweep. ISystemTeamSync returns a report, so it can't implement
        // IRecurringJob; Hangfire resolves the implementation (SystemTeamSyncJob) from DI.
        // Controlled by SyncServiceSettings (Admin/SyncSettings) — set service mode to "None"
        // to disable without redeploying.
        yield return new RecurringJobDescriptor("teams-system-sync", typeof(ISystemTeamSync), "0 * * * *");
    }
}
