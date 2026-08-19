using Humans.Base.Interfaces;
using Humans.Expenses.Jobs;

namespace Humans.Expenses;

/// <summary>Expenses' recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        // Push approved expense reports to Holded as purchase documents.
        yield return new RecurringJobDescriptor(
            "expenses-holded-outbox", typeof(HoldedExpenseOutboxJob), "*/1 * * * *");
    }
}
