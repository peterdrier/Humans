using System.Globalization;
using Humans.AuditLog.Contracts;
using Humans.AuditLog.Data;
using Humans.AuditLog.Services;
using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.AuditLog;

public sealed class Section : ISection, ISectionAdminNav, ISectionAdminTiles
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

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Audit", [
            new("Audit log", "AuditLog", "Index", null, null, "fa-solid fa-book-open", PolicyNames.BoardOrAdmin)
        ])
    ];

    IEnumerable<AdminTile> ISectionAdminTiles.Tiles() =>
    [
        new AdminTile("auditlog.total", "Audit events", "fa-solid fa-list-check", TotalAsync, Weight: 100)
    ];

    // Page size 1, no filter: TotalCount is the tile.
    private static async ValueTask<AdminTileValue?> TotalAsync(IServiceProvider sp, CancellationToken ct)
    {
        var total = (await sp.GetRequiredService<AuditViewerService>().GetPageAsync(null, 1, 1, ct)).TotalCount;
        return new AdminTileValue(total.ToString("N0", CultureInfo.CurrentCulture), Detail: "every automated action, logged");
    }
}
