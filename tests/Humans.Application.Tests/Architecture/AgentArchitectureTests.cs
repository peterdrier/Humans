using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Agent;
using Humans.Infrastructure.Services.Agent;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests pinning the §15 layering for the Agent section
/// (design-rules §15i Agent entry). The Agent section has one Application-layer
/// orchestrator (<see cref="AgentService"/>) plus Infrastructure-resident
/// helpers that bridge to filesystem preload readers, the Anthropic SDK, and
/// per-user snapshot composition:
/// <list type="bullet">
///   <item><description><c>AgentService</c> lives in <c>Humans.Application.Services.Agent</c> and goes through <see cref="IAgentRepository"/>.</description></item>
///   <item><description><c>AgentService</c> is in <c>Humans.Application</c>, which structurally cannot import EF Core.</description></item>
///   <item><description>Agent helper services (<c>AgentAbuseDetector</c>, <c>AgentPromptAssembler</c>, <c>AgentSettingsService</c>, <c>AgentToolDispatcher</c>, <c>AgentUserSnapshotProvider</c>) live in <c>Humans.Infrastructure.Services.Agent</c> because they depend on Infrastructure-side preload readers / Anthropic bridges.</description></item>
///   <item><description><see cref="IAgentRepository"/> has a sealed EF-backed implementation.</description></item>
/// </list>
/// </summary>
public class AgentArchitectureTests
{
    // ── AgentService (Application) ───────────────────────────────────────────

    [HumansFact]
    public void AgentService_TakesRepository()
    {
        var ctor = typeof(AgentService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IAgentRepository),
            because: "§15 requires every section service to go through its owning repository interface");
    }

    [HumansFact]
    public void AgentService_LivesInApplicationServicesAgentNamespace()
    {
        typeof(AgentService).Namespace
            .Should().Be("Humans.Application.Services.Agent",
                because: "the orchestrator service lives in the Application layer per design-rules §15");
    }

    [HumansFact]
    public void AgentService_AssemblyIsHumansApplication()
    {
        typeof(AgentService).Assembly.GetName().Name
            .Should().Be("Humans.Application",
                because: "Humans.Application structurally forbids EF Core references, so the orchestrator cannot import EF even if a future typo tries");
    }

    [HumansFact]
    public void AgentService_DoesNotReferenceEntityFrameworkCore()
    {
        var apiAssembly = typeof(AgentService).Assembly;
        var referencedAssemblies = apiAssembly.GetReferencedAssemblies();

        referencedAssemblies
            .Should().NotContain(
                a => string.Equals(a.Name, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
                because: "services in Humans.Application must not import EF Core (design-rules §2b)");
    }

    // ── IAgentRepository implementation ──────────────────────────────────────

    [HumansFact]
    public void AgentRepository_IsSealed()
    {
        var repoType = typeof(AgentAbuseDetector).Assembly
            .GetTypes()
            .Single(t => !t.IsAbstract && typeof(IAgentRepository).IsAssignableFrom(t));
        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    [HumansFact]
    public void AgentRepository_ImplementationLivesInHumansInfrastructure()
    {
        // The EF-backed implementation sits under Humans.Infrastructure (it
        // currently lives directly under Humans.Infrastructure.Repositories
        // rather than a per-section subfolder — see the design-rules §15b
        // follow-up. This test pins the assembly only so a future move into
        // Repositories.Agent doesn't trip the test, while still preventing the
        // implementation from leaking back into Application.)
        var infraAssembly = typeof(AgentAbuseDetector).Assembly;
        var impl = infraAssembly.GetTypes()
            .SingleOrDefault(t => !t.IsAbstract && typeof(IAgentRepository).IsAssignableFrom(t));

        impl.Should().NotBeNull(
            because: "the EF-backed AgentRepository lives in Humans.Infrastructure");
        impl!.Assembly.GetName().Name
            .Should().Be("Humans.Infrastructure",
                because: "EF-backed repositories live in Humans.Infrastructure per design-rules §15b");
    }

    // ── Agent helpers stay in Infrastructure ─────────────────────────────────

    [HumansFact]
    public void AgentHelpers_LiveInInfrastructureServicesAgentNamespace()
    {
        // These types bridge to filesystem preload readers / Anthropic SDK and
        // therefore stay in Infrastructure per the §15i Agent entry. The
        // namespace check makes a regression where one of them migrates to
        // Humans.Application without the supporting bridge abstractions land
        // visibly in this test.
        var helperTypes = new[]
        {
            typeof(AgentAbuseDetector),
            typeof(AgentPromptAssembler),
            typeof(AgentSettingsService),
            typeof(AgentToolDispatcher),
            typeof(AgentUserSnapshotProvider),
        };

        foreach (var t in helperTypes)
        {
            t.Namespace
                .Should().Be("Humans.Infrastructure.Services.Agent",
                    because: $"{t.Name} bridges Infrastructure concerns (preload readers / Anthropic SDK / cross-section snapshot composition) and stays in Infrastructure per design-rules §15i Agent entry");
            t.Assembly.GetName().Name
                .Should().Be("Humans.Infrastructure",
                    because: $"{t.Name} must remain in Humans.Infrastructure until its Infrastructure dependencies are abstracted behind Application-side interfaces");
        }
    }

    // ── Helper-interface contracts ───────────────────────────────────────────

    [HumansFact]
    public void AgentHelperInterfaces_LiveInApplication()
    {
        // The contracts the helpers implement (IAgentAbuseDetector,
        // IAgentPromptAssembler, IAgentSettingsService, IAgentToolDispatcher,
        // IAgentUserSnapshotProvider) live in Humans.Application — that's
        // what lets AgentService inject them without crossing the layer.
        var contracts = new[]
        {
            typeof(IAgentAbuseDetector),
            typeof(IAgentPromptAssembler),
            typeof(IAgentSettingsService),
            typeof(IAgentToolDispatcher),
            typeof(IAgentUserSnapshotProvider),
        };

        foreach (var c in contracts)
        {
            c.Assembly.GetName().Name
                .Should().Be("Humans.Application",
                    because: $"{c.Name} contract is declared in the Application layer so the orchestrator can depend on it without crossing into Infrastructure");
        }
    }
}
