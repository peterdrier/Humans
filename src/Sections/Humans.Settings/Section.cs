using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Base.Interfaces.Caching;
using Humans.Settings.Contracts;
using Humans.Settings.Data;
using Humans.Settings.Services;
using Humans.Settings.ViewComponents;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Humans.AuditLog.Contracts;

namespace Humans.Settings;

public sealed class Section : ISection, ISectionAdminNav, ISectionChrome, ISectionSettings
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, SettingsAuditActions>();
        // Sentinel stays the pre-rename "system_settings": the history table was renamed too, so an existing database
        // reads an empty history. The sentinel must name a table that exists before this section's pending migrations.
        services.AddSectionDbContext<SettingsDbContext>(sentinelTable: "system_settings");

        services.AddSingleton<ISettingsRepository, Repository>();

        services.AddKeyedScoped<ISettingsWriteService, Service>(CachingSettingsService.InnerServiceKey);
        services.AddSingleton<CachingSettingsService>();
        services.AddSingleton<ISettingsWriteService>(sp => sp.GetRequiredService<CachingSettingsService>());
        services.AddSingleton<ISettingsService>(sp => sp.GetRequiredService<CachingSettingsService>());
        services.AddSingleton<IEventSettingsSeeding>(sp => sp.GetRequiredService<CachingSettingsService>());
        services.AddSingleton<ICacheStats>(sp => sp.GetRequiredService<CachingSettingsService>());
        services.AddSingleton<IHostedService>(sp => sp.GetRequiredService<CachingSettingsService>());
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Settings", [
            new("Event settings", "Settings", "Index", null, null, "fa-solid fa-calendar-days", PolicyNames.AdminOnly, Weight: 0)
        ])
    ];

    IEnumerable<ChromeComponent> ISectionChrome.Components() =>
        [new(ChromeSlots.UserMenu, typeof(SettingsUserMenuViewComponent), Weight: 10)];

    IEnumerable<SettingsTab> ISectionSettings.Tabs() =>
    [
        new SettingsTab("event", "Settings_TabEvent", typeof(EventSettingsTabViewComponent), Weight: int.MinValue)
    ];
}
