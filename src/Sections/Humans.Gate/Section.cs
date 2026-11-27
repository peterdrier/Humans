using Humans.Tickets.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Gate.Contracts;
using Humans.Gate.Data;
using Humans.Gate.Domain;
using Humans.Gate.Services;
using Humans.Gate.Services.Stores;
using Humans.Gate.ViewComponents;
using Humans.Gdpr.Contracts;
using Humans.Settings.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;
using Humans.Base.Models.Tables;
using Humans.Gate.Controllers;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

namespace Humans.Gate;

/// <summary>No caching decorator: a stale gate read admits or blocks the wrong person.</summary>
public sealed class Section : SectionBase, ISectionAdminNav, ISectionSettings
{
    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, GateAuditActions>();
        services.AddSectionDbContext<GateDbContext>(sentinelTable: "gate_settings");

        services.AddSingleton<IGateRepository, GateRepository>();

        services.AddSingleton<GatePinThrottle>();

        services.AddScoped<GateService>();
        services.AddScoped<IGateService>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IRetentionPeriodContributor>(sp => sp.GetRequiredService<GateService>());
        services.AddScoped<IGateAdmissions, GateAdmissionReads>();

        // Only admit verdicts reach the arrivals report's badge column.
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [GateVerdict.Admitted] = "bg-success",
            [GateVerdict.AdmittedEarly] = "bg-success",
            [GateVerdict.AdmittedEarlyOverride] = "bg-warning text-dark",
            [GateVerdict.AdmittedChildWithAdult] = "bg-warning text-dark",
        });

    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        AdminGroup([
            new("Terminal",   "TicketsGateAdmin", "Index", null, null, "fa-solid fa-key",     TicketsPolicies.Read),
            AdminLink<GateReportController>("Arrivals", nameof(GateReportController.Index), "fa-solid fa-person-walking-arrow-right", TicketsPolicies.Read)
        ])
    ];

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("gate", "Settings_TabGate", typeof(GateSettingsTabViewComponent), TicketsPolicies.Manage)
    ];
}
