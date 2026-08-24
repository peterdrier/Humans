using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Debug;

/// <summary>
/// Debug's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <see cref="Register"/> is empty: every dependency the diagnostics pages read is a Base
/// singleton registered by its owner —
/// <c>IClientStatsTracker</c>, <c>IHttpStatusTracker</c>, <c>ConfigurationRegistry</c>,
/// <c>QueryStatistics</c>, <c>ICacheStatsProvider</c>, the <c>ICacheStats</c> decorator fan-in
/// and <c>IAdminDatabaseDiagnosticsService</c>. The section owns no tables, so there is no
/// <c>AddSectionDbContext</c> call and no repository. The class still ships: <see cref="ISection"/>
/// is what puts the assembly in <c>SectionDiscoveryExtensions</c>'s discovered-sections log,
/// which is the first thing to read when a section's page 404s. Access is the <c>AdminOnly</c>
/// policy, which stays in Shell's <c>AuthorizationPolicyExtensions</c> (design §8).
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Nothing to register: every dependency the diagnostics pages read is a Base
        // singleton owned by someone else, and the log-reading API moved to Backdoor
        // (nobodies-collective/Humans#1128). The class still ships — see the remarks.
    }
}
