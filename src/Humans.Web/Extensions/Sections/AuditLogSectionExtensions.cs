using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.AuditLog;
using AuditLogAuditLogService = Humans.Application.Services.AuditLog.AuditLogService;

namespace Humans.Web.Extensions.Sections;

internal static class AuditLogSectionExtensions
{
    internal static IServiceCollection AddAuditLogSection(this IServiceCollection services)
    {
        // Audit Log section — §15 repository pattern (issue #552).
        // Append-only per design-rules §12. No decorator — writes are scattered
        // across every section and reads are admin-only, so a cache is not
        // warranted (same rationale as Governance/User/Budget/City Planning).
        // IAuditLogRepository is Singleton (IDbContextFactory-based) so the
        // service can inject it directly.
        services.AddSingleton<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<AuditLogAuditLogService>();
        services.AddScoped<IAuditLogService>(sp => sp.GetRequiredService<AuditLogAuditLogService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AuditLogAuditLogService>());

        return services;
    }
}
