using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Agent.Data;
using Humans.Agent.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Agent.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Agent
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/AgentArchitectureTests.cs</c>, whose three
/// tests all pinned the Application/Infrastructure split the section no longer has: they
/// asserted that <c>AgentService</c> sat in <c>Humans.Application</c> without EF Core, that the
/// five helpers sat in <c>Humans.Infrastructure.Services.Agent</c>, and that their interfaces
/// sat in <c>Humans.Application</c>. One assembly with one internal surface subsumes all three
/// (design §15 step 11).
/// </remarks>
public class AgentArchitectureTests
{
    /// <summary>
    /// Pins the set of types that may inject <see cref="IAgentRepository"/>: the owning service
    /// and the repository implementation. A new consumer taking the repository directly would
    /// bypass the service layer and the single-writer rule for the <c>agent_*</c> tables — which
    /// is exactly what <c>AgentConversationRetentionJob</c> used to do from Base.
    /// </summary>
    [HumansFact]
    public void IAgentRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            "Humans.Agent.Services.AgentService",
            "Humans.Agent.Services.AgentSettingsService",
            "Humans.Agent.Services.AgentAdminStatusService",
            "Humans.Agent.Data.AgentRepository",
        };

        var consumers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(IAgentRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        consumers.Where(c => !allowed.Contains(c)).Should().BeEmpty(
            because: "every read/write to the agent_* tables must go through the section's services");
    }

    [HumansFact]
    public void AgentEntities_HaveNoCrossSectionNavigationProperties()
    {
        typeof(AgentConversation).GetProperty("User").Should().BeNull(
            because: "cross-domain references are bare Guid FKs with no nav (design-rules §6c); "
                   + "resolve via IUserServiceRead");
        typeof(AgentMessage).GetProperty("HandedOffToFeedback").Should().BeNull(
            because: "HandedOffToFeedbackId is a bare Guid pointing at a Feedback-owned row");

        typeof(AgentConversation).GetProperty("UserId").Should().NotBeNull();
        typeof(AgentMessage).GetProperty("HandedOffToFeedbackId").Should().NotBeNull();
    }

    [HumansFact]
    public void SectionRegistersTheConversationRetentionForwarder()
    {
        // The daily job that deletes expired agent conversations asks for this interface.
        // Lose the registration and the job stops running, so old conversations are kept
        // forever. The job itself is not in DI, so no start-up check would notice.
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        services.Should().ContainSingle(d => d.ServiceType == typeof(IAgentConversationRetention));
    }
}
