using Humans.Base.Interfaces;
using Humans.Base.Hosting;
using Humans.Settings.Contracts;
using Humans.Settings.Data;
using Humans.Settings.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Settings;

/// <summary>
/// Settings' DI entry point, at the project root by convention. Discovered by
/// Shell — nothing names it, so it needs no section prefix.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        // Sentinel stays "system_settings" — the pre-rename table name. Renaming the
        // context renamed its history table too (__EFMigrationsHistory_SystemSettings ->
        // __EFMigrationsHistory_Settings), so on an existing database the section reads an
        // empty history and would re-execute its baseline. The sentinel is what tells
        // SectionMigrationRunner "the tables are already there" — so it must name a table
        // that exists *before* this section's pending migrations run.
        services.AddSectionDbContext<SettingsDbContext>(sentinelTable: "system_settings");

        // Repository uses IDbContextFactory<SettingsDbContext> so it can be Singleton;
        // every method opens its own short-lived DbContext.
        services.AddSingleton<ISettingsRepository, Repository>();

        // Both interfaces resolve the same implementation: outside sections take
        // ISettingsServiceRead, the ones that genuinely write take ISettingsService
        // and declare it with [CrossSectionWrite]
        // (memory/architecture/section-read-write-split.md).
        services.AddScoped<ISettingsService, Service>();
        services.AddScoped<ISettingsServiceRead>(sp => sp.GetRequiredService<ISettingsService>());
    }
}
