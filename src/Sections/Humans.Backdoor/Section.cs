using Humans.Backdoor.Data;
using Humans.Backdoor.Filters;
using Humans.Backdoor.Services;
using Humans.Base.Authorization;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Humans.AuditLog.Contracts;

namespace Humans.Backdoor;

public sealed class Section : ISection, ISectionAdminNav
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IAuditActionContributor, BackdoorAuditActions>();
        services.AddSectionDbContext<BackdoorDbContext>(sentinelTable: "backdoor_api_keys");

        services.AddSingleton<IBackdoorApiKeyRepository, BackdoorApiKeyRepository>();
        services.AddScoped<BackdoorApiKeyService>();
        services.AddScoped<IBackdoorApiKeyService>(sp => sp.GetRequiredService<BackdoorApiKeyService>());
        services.AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<BackdoorApiKeyService>());
        services.AddScoped<IUserMerge>(sp => sp.GetRequiredService<BackdoorApiKeyService>());

        // [ServiceFilter] resolves the filter from the container, so it must be registered.
        services.AddScoped<BackdoorApiKeyAuthFilter>();
    }

    IEnumerable<AdminNavGroup> ISectionAdminNav.Groups() =>
    [
        new("Backdoor", [
            new("API keys", "Backdoor", "Index", null, null, "fa-solid fa-key", PolicyNames.BoardOrAdmin)
        ])
    ];
}
