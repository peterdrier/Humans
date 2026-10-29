using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Data;
using Humans.AuditLog.Services;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.AuditLog;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<AuditLogDbContext>(sentinelTable: "audit_log");

        services.AddSingleton<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<IAuditLogService>(sp => sp.GetRequiredService<AuditLogService>());
        services.AddScoped<IAuditLogReader>(sp => sp.GetRequiredService<AuditLogService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AuditLogService>());
        // Temporary: goes with the six Google columns.
        services.AddScoped<ILegacyGoogleSyncAuditReader>(sp => sp.GetRequiredService<AuditLogService>());

        services.AddScoped<AuditViewerService>();
        services.AddScoped<IAuditViewerService>(sp => sp.GetRequiredService<AuditViewerService>());
    }
}
