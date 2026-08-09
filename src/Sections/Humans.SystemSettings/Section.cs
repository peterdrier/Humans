using Humans.Application.Interfaces;
using Humans.Infrastructure.Hosting;
using Humans.SystemSettings.Contracts;
using Humans.SystemSettings.Data;
using Humans.SystemSettings.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.SystemSettings;

/// <summary>
/// SystemSettings' DI entry point, at the project root by convention. Discovered by
/// Shell — nothing names it, so it needs no section prefix.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<SystemSettingsDbContext>(sentinelTable: "system_settings");

        // Repository uses IDbContextFactory<SystemSettingsDbContext> so it can be Singleton;
        // every method opens its own short-lived DbContext.
        services.AddSingleton<ISystemSettingsRepository, Repository>();

        // ISystemSettingsService is the section's Contracts/ surface — Email and
        // GoogleIntegration both call it — so unlike Store the interface stays (§6a).
        services.AddScoped<ISystemSettingsService, Service>();
    }
}
