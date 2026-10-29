using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Gate.Data;
using Humans.Gate.Domain;
using Humans.Gate.Jobs;
using Humans.Gate.Services;
using Humans.Gate.Services.Stores;
using Humans.Gate.ViewComponents;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Gate;

/// <summary>No caching decorator: a stale gate read admits or blocks the wrong person.</summary>
public sealed class Section : ISection, ISectionAdminNav, ISectionJobs, ISectionSettings
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, GateAuditActions>();
        services.AddSectionDbContext<GateDbContext>(sentinelTable: "gate_settings");

        services.AddSingleton<IGateRepository, GateRepository>();

        // Identity registers only IPasswordHasher<User>, so register the GateStaffPin one.
        services.AddSingleton<IPasswordHasher<GateStaffPin>, PasswordHasher<GateStaffPin>>();

        services.AddSingleton<GatePinThrottle>();
        services.AddSingleton<GateVendorMirrorLedger>();

        services.AddScoped<GateService>();
        services.AddScoped<IGateService>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<GateService>());

        services.AddScoped<GateRetentionJob>();
        services.AddScoped<GateVendorCheckInJob>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Gate", [
            new("Terminal",   "TicketsGateAdmin", "Index", null, null, "fa-solid fa-key",     PolicyNames.TicketAdminOrAdmin),
            new("Vendor check-in backfill", "GateVendorBackfillAdmin", "Index", null, null, "fa-solid fa-cloud-arrow-up", PolicyNames.AdminOnly)
        ])
    ];

    IEnumerable<RecurringJobDescriptor> ISectionJobs.Jobs(IServiceProvider services)
    {
        yield return new RecurringJobDescriptor(
            "gate-retention", typeof(GateRetentionJob), "45 3 * * *");
    }

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("gate", "Settings_TabGate", typeof(GateSettingsTabViewComponent), PolicyNames.TicketAdminOrAdmin)
    ];
}
