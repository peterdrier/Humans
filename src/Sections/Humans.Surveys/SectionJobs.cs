using Humans.Base.Interfaces;
using Humans.Surveys.Jobs;

namespace Humans.Surveys;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "surveys-reminder", typeof(SendSurveyReminderJob), "0 9 * * *");
    }
}
