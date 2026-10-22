using Humans.Base.Interfaces;
using Humans.Expenses.Jobs;

namespace Humans.Expenses;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "expenses-holded-outbox", typeof(HoldedExpenseOutboxJob), "*/5 * * * *");
    }
}
