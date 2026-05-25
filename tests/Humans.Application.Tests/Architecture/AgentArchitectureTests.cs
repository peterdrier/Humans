using AwesomeAssertions;
using Humans.Application.Interfaces;
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

    // TakesRepository check covered by pattern G (positive wiring noise).
    // Service-namespace check covered by HUM0012.
    // AssemblyIsHumansApplication check covered by HUM0012.

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

    // Sealed-repository check covered by IRepositoryImplementationsAreSealedRule.
    // Infrastructure-assembly check covered by RepositoryImplementationsLiveInInfrastructureRule.

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
