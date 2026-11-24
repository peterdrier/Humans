using Humans.Tickets.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Gate.Contracts;
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
using Humans.Audit.Contracts;
using Humans.Base.Models.Tables;

[assembly: Humans.Base.Demo.NoDemoStory("No demo story written yet.")]

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
        services.AddScoped<IGateAdmissions, GateAdmissionReads>();

        // Only admit verdicts reach the arrivals report's badge column.
        EnumBadgeMap.Register(new Dictionary<Enum, string>
        {
            [GateVerdict.Admitted] = "bg-success",
            [GateVerdict.AdmittedEarly] = "bg-success",
            [GateVerdict.AdmittedEarlyOverride] = "bg-warning text-dark",
            [GateVerdict.AdmittedChildWithAdult] = "bg-warning text-dark",
        });

        services.AddScoped<GateRetentionJob>();
        services.AddScoped<GateVendorCheckInJob>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Gate", [
            new("Terminal",   "TicketsGateAdmin", "Index", null, null, "fa-solid fa-key",     TicketsPolicies.Read),
            new("Arrivals",   "GateReport",       "Index", null, null, "fa-solid fa-person-walking-arrow-right", TicketsPolicies.Read),
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
        new SettingsTab("gate", "Settings_TabGate", typeof(GateSettingsTabViewComponent), TicketsPolicies.Manage)
    ];
}
