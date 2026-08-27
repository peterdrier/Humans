using Humans.Surveys.Contracts;
using Humans.Base.Interfaces;
using Humans.Surveys.Jobs;

namespace Humans.Surveys;

/// <summary>Surveys' recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "surveys-reminder", typeof(SendSurveyReminderJob), "0 9 * * *");
    }
}
