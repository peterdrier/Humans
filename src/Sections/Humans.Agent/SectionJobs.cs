using Humans.Base.Interfaces;
using Humans.Agent.Jobs;

namespace Humans.Agent;

/// <summary>Agent's recurring jobs. Discovered by Shell — nothing names it, so it needs no section prefix.</summary>
internal sealed class SectionJobs : ISectionJobs
{
    public IEnumerable<RecurringJobDescriptor> Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "agent-conversation-retention", typeof(AgentConversationRetentionJob), "15 3 * * *");
    }
}
