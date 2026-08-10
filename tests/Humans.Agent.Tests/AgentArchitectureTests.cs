using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Agent.Data;
using Humans.Agent.Domain;
using Humans.Agent.Services;
using Humans.Application.Interfaces.Gdpr;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;

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
    [HumansFact]
    public void OnlySectionAndResourceArePublic()
    {
        // "Public means Section or Contracts/" (design §15 step 5), now enforced at build time
        // by HUM0034. AgentResource is the one sanctioned extra: the boot localization
        // diagnostic discovers section resource markers through GetExportedTypes(), so an
        // internal marker is skipped in silence (§15 step 3b).
        //
        // All three controllers are internal. Shell registers SectionControllerFeatureProvider,
        // which relaxes MVC's IsPublic check for assemblies carrying [assembly: Section("…")]
        // (memory/architecture/section-controllers-need-feature-provider.md — which says in as
        // many words: do not "fix" a 404 by making the controller public).
        //
        // Generated migration classes are emitted `public partial` by `dotnet ef` and are never
        // hand-edited (memory/process/never-hand-edit-migrations); they are excluded rather
        // than internalized.
        var publicTypes = typeof(Section).Assembly.GetExportedTypes()
            .Where(t => !string.Equals(t.Namespace, "Humans.Agent.Data.Migrations", StringComparison.Ordinal))
            .Select(t => t.FullName)
            .Order(StringComparer.Ordinal)
            .ToList();

        publicTypes.Should().BeEquivalentTo(
        [
            "Humans.Agent.AgentResource",
            "Humans.Agent.Section",
        ]);
    }

    [HumansFact]
    public void SectionControllersAreInternal()
    {
        var controllers = typeof(Section).Assembly.GetTypes()
            .Where(t => t.Name.EndsWith("Controller", StringComparison.Ordinal))
            .ToList();

        controllers.Should().HaveCount(3);
        controllers.Should().OnlyContain(t => !t.IsPublic);
    }

    [HumansFact]
    public void ContractsExposeOnlyTheCrossSectionSurface()
    {
        // Pins the whole Contracts assembly, so widening Agent's cross-section surface is a
        // visible diff rather than a silent one. Four types, one consumer story each:
        //   IAgentConversationRetention — AgentConversationRetentionJob, which stays in Base
        //     because recurring jobs have no discovery seam yet (§15 step 6b).
        //   IAgentAvailability          — Shell's HelpWidget and the two agent health checks.
        //   IAgentPreloadAugmentor      — implemented in Shell, consumed by the section.
        //   AgentSectionKeys            — Shell's AgentPreloadAugmentor resolves glossary
        //                                 headings onto fetch_section_guide keys.
        var contractTypes = typeof(IAgentAvailability).Assembly.GetExportedTypes()
            .Select(t => t.Name)
            .Order(StringComparer.Ordinal)
            .ToList();

        contractTypes.Should().BeEquivalentTo(
        [
            "AgentSectionKeys",
            "IAgentAvailability",
            "IAgentConversationRetention",
            "IAgentPreloadAugmentor",
        ]);
    }

    [HumansFact]
    public void ContractsReferenceOnlyTheBottomOfTheGraph()
    {
        typeof(IAgentAvailability).Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Humans.Application" || a.Name == "Humans.Domain",
                because: "a section's contracts leaf references only the bottom of the graph "
                       + "(memory/architecture/section-project-cycle-fix.md)");
    }

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
    public void ServiceImplementsIUserDataContributor()
    {
        typeof(IUserDataContributor).IsAssignableFrom(typeof(AgentService))
            .Should().BeTrue(
                because: "Agent owns agent_conversations and agent_messages (user-scoped); it must "
                       + "contribute to the GDPR Article 15 export");
    }

    [HumansFact]
    public void SectionRegistersTheContractsAndTheGdprContributor()
    {
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        services.Single(d => d.ServiceType == typeof(IAgentRepository)).Lifetime
            .Should().Be(ServiceLifetime.Scoped);
        services.Should().ContainSingle(d => d.ServiceType == typeof(IAgentAvailability));
        services.Should().ContainSingle(d => d.ServiceType == typeof(IAgentConversationRetention));
        services.Should().ContainSingle(d => d.ServiceType == typeof(IUserDataContributor));
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

    [HumansFact]
    public void ControllersKeepTheirRoutePrefixes()
    {
        // The chat history, the admin console and the key-authed review API all keep the URLs
        // they had in Shell — a G5 move changes files, never routes.
        RoutePrefixOf("Humans.Agent.Controllers.AgentController").Should().Be("Agent");
        RoutePrefixOf("Humans.Agent.Controllers.AdminAgentController").Should().Be("Agent/Admin");
        RoutePrefixOf("Humans.Agent.Controllers.AgentApiController").Should().Be("api/agent");
    }

    private static string RoutePrefixOf(string fullName)
    {
        var type = typeof(Section).Assembly.GetType(fullName, throwOnError: true)!;
        return type
            .GetCustomAttributes(typeof(Microsoft.AspNetCore.Mvc.RouteAttribute), inherit: false)
            .Cast<Microsoft.AspNetCore.Mvc.RouteAttribute>()
            .Single().Template;
    }
}
