using Humans.Base.Interfaces;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Data;
using Humans.AuditLog.Services;
using Humans.Gdpr.Contracts;
using Humans.Base.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.AuditLog;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<AuditLogDbContext>(sentinelTable: "audit_log");

        services.AddSingleton<AuditActionRegistry>();
        // Constructed at startup so a duplicate or over-long action name fails the boot, not the first audit page.
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<AuditActionRegistry>());

        services.AddSingleton<IAuditLogRepository, AuditLogRepository>();
        services.AddScoped<AuditLogService>();
        services.AddScoped<IAuditLogService>(sp => sp.GetRequiredService<AuditLogService>());
        services.AddScoped<IAuditLogReader>(sp => sp.GetRequiredService<AuditLogService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<AuditLogService>());
        // Temporary: goes with the six Google columns.
        services.AddScoped<ILegacyGoogleSyncAuditReader>(sp => sp.GetRequiredService<AuditLogService>());

        services.AddScoped<IAuditViewerService, AuditViewerService>();
    }
}
