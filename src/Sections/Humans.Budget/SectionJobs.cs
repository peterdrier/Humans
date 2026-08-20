using Humans.Base.Interfaces;
using Humans.Budget.Jobs;

namespace Humans.Budget;

/// <summary>Budget's recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // Materialize ticket sales actuals into budget line items.
        yield return new RecurringJobDescriptor(
            "budget-ticketing-sync", typeof(TicketingBudgetSyncJob), "30 4 * * *");
    }
}
