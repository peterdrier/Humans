using Humans.GoogleIntegration.Contracts;
using Humans.Application.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Data;
using Humans.AuditLog.Services;
using Humans.Gdpr.Contracts;
using Humans.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.AuditLog;

/// <summary>
/// Audit Log's DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
/// <remarks>
/// <para>
/// No caching decorator (Option A): writes are scattered across every section (~130 call
/// sites) and reads are admin-only, so a cache is not warranted — the same rationale Users
/// (#243), Governance (#242), Budget (#544) and City Planning (#543) used.
/// </para>
/// <para>
/// The read+render owner — <c>IAuditViewerService</c> / <c>AuditEvent</c> — is registered
/// by Shell, not here. It resolves actor, subject and team display names through
/// <c>IUserServiceRead</c>, <c>ITeamServiceRead</c> and <c>ITeamResourceService</c>, which
/// makes it a cross-section orchestrator; AuditLog is a horizontal section and
/// <c>peters-hard-rules.md</c> forbids one from referencing a vertical.
/// </para>
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<AuditLogDbContext>(sentinelTable: "audit_log");

        // §15 repository pattern (issue #552): Singleton + IDbContextFactory (§15b) so the
        // repository owns context lifetime and the service can inject it directly.
        // Append-only per design-rules §12 — the repository exposes no update or delete.
        services.AddSingleton<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<IAuditLogService>(sp => sp.GetRequiredService<AuditLogService>());
        // Audit rows carry an actor id → GDPR export contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AuditLogService>());
    }
}
