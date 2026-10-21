using Humans.Base.Interfaces;
using Humans.Feedback.Services;

namespace Humans.Feedback;

/// <summary>Retries persisted reply emails through Feedback's delivery pipeline.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor("feedback-reply-emails", typeof(FeedbackReplyDelivery), "* * * * *");
    }
}
