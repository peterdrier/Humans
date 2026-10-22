using Humans.Base.Interfaces;
using Humans.Budget.Jobs;

namespace Humans.Budget;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "budget-ticketing-sync", typeof(TicketingBudgetSyncJob), "30 4 * * *");
    }
}
