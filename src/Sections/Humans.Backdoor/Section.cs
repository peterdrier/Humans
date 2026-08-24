using Humans.Backdoor.Data;
using Humans.Backdoor.Filters;
using Humans.Backdoor.Services;
using Humans.Base.Hosting;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Backdoor;

/// <summary>
/// Backdoor's DI entry point, at the project root by convention. Registers the one table
/// (<c>backdoor_api_keys</c>), the key service behind it, and the single auth filter every
/// <c>/api/backdoor/*</c> controller hangs off (nobodies-collective/Humans#1128).
/// </summary>
/// <remarks>
/// The five machine controllers need no registration of their own — Shell discovers them
/// as application parts. Nothing here touches the four served sections: Backdoor reaches
/// them only through the contracts interfaces their own <c>Section.cs</c> files register.
/// </remarks>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<BackdoorDbContext>(sentinelTable: "backdoor_api_keys");

        // §15 repository pattern: Singleton + IDbContextFactory (§15b) so the repository
        // owns context lifetime.
        services.AddSingleton<IBackdoorApiKeyRepository, BackdoorApiKeyRepository>();
        services.AddScoped<IBackdoorApiKeyService, BackdoorApiKeyService>();

        // [ServiceFilter] resolves the filter from the container, so it must be registered.
        services.AddScoped<BackdoorApiKeyAuthFilter>();
    }
}
