using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Agent.Data;
using Humans.Agent.Domain;
using Humans.Agent.Services;
using Humans.Gdpr.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Humans.Users.Contracts;

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
    public void SectionTypesLocalizeThroughTheSectionsOwnResourceSet()
    {
        // The carve moved every section-owned Agent_* key out of SharedResource, so a type still
        // injecting IStringLocalizer<SharedResource> would resolve nothing and render the raw
        // key — a 200 with degraded copy, in every language. Surveys shipped exactly that on
        // three controller paths past four green render tests (peterdrier/Humans#1251), because
        // controller-resolved copy sits on failure paths fixtures do not reach. Agent has
        // controllers on both /Agent and /Agent/Admin, so it is asserted structurally.
        var offenders = typeof(Section).Assembly.GetTypes()
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()
                .Where(p => p.ParameterType.IsGenericType
                         && p.ParameterType.GetGenericTypeDefinition() == typeof(IStringLocalizer<>)
                         && p.ParameterType.GetGenericArguments()[0] != typeof(AgentResource))
                .Select(p => $"{t.FullName} takes IStringLocalizer<{p.ParameterType.GetGenericArguments()[0].Name}>")))
            .Order(StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "every carved Agent_* key lives in AgentResource; resolving one through "
                   + "another set renders the key itself and no error (§15 step 3b)");
    }


}
