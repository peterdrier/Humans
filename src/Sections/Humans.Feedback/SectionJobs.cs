using Humans.Base.Interfaces;
using Humans.Feedback.Services;

namespace Humans.Feedback;

/// <summary>Retries persisted reply emails through the existing Feedback service.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor("feedback-reply-emails", typeof(FeedbackService), "* * * * *");
    }
}
