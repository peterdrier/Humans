using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Data;
using Humans.AuditLog.Services;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
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
/// here since G5 lane 4b-2h (nobodies-collective/Humans#866). Actor, subject and team names
/// come from the <c>IEntityNameContributor</c> fan-out in Base, so the section names no
/// vertical it renders (nobodies-collective/Humans#1059).
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
        // Section-internal reads (AuditViewerService only) — off the cross-section contract.
        services.AddScoped<IAuditLogReader>(sp => sp.GetRequiredService<AuditLogService>());
        // Audit rows carry an actor id → GDPR export contributor (design-rules §8a).
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AuditLogService>());

        // Read+render owner: wraps the raw entry queries with actor/subject/team name
        // resolution. No DB, no cache — see AuditViewerService.
        services.AddScoped<IAuditViewerService, AuditViewerService>();
    }
}
