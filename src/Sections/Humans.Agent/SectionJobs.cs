using Humans.Base.Interfaces;
using Humans.Agent.Jobs;

namespace Humans.Agent;

internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "agent-conversation-retention", typeof(AgentConversationRetentionJob), "15 3 * * *");
    }
}
