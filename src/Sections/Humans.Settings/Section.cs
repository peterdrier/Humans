using Humans.Base.Interfaces;
using Humans.Base.Hosting;
using Humans.Settings.Contracts;
using Humans.Settings.Data;
using Humans.Settings.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Settings;

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Sentinel stays the pre-rename "system_settings": the history table was renamed too, so an existing database
        // reads an empty history. The sentinel must name a table that exists before this section's pending migrations.
        services.AddSectionDbContext<SettingsDbContext>(sentinelTable: "system_settings");

        services.AddSingleton<ISettingsRepository, Repository>();

        services.AddScoped<ISettingsWriteService, Service>();
        services.AddScoped<ISettingsService>(sp => sp.GetRequiredService<ISettingsWriteService>());
        services.AddScoped<IEventSettingsSeeding>(sp => (Service)sp.GetRequiredService<ISettingsWriteService>());
    }
}
