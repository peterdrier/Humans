using Humans.Base.Interfaces;
using Humans.Teams.Contracts;

namespace Humans.Teams;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // ISystemTeamSync returns a report, so it can't implement IRecurringJob. Disable it without a redeploy by setting
        // its service mode to "None" in Admin/SyncSettings.
        yield return new RecurringJobDescriptor("teams-system-sync", typeof(ISystemTeamSync), "0 * * * *");
    }
}
