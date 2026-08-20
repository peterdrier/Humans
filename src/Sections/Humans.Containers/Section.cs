using Humans.Base.Interfaces;
using Humans.Containers.Authorization;
using Humans.Containers.Contracts;
using Humans.Containers.Data;
using Humans.Containers.Services;
using Humans.Base.Hosting;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Containers;

/// <summary>
/// Containers' DI entry point, at the project root by convention. Discovered by Shell —
/// nothing names it, so it needs no section prefix.
/// </summary>
public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<ContainersDbContext>(sentinelTable: "containers");

        services.AddSingleton<IContainerRepository, Repository>();
        services.AddScoped<IContainerService, Service>();

        // Resource-based handler moves with the section; the policy registration stays
        // in Shell's AuthorizationPolicyExtensions (design §8, §15 step 6).
        services.AddScoped<IAuthorizationHandler, ContainerAuthorizationHandler>();
    }
}
