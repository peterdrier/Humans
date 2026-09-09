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

public sealed class Section : ISection
{
    public void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddSectionDbContext<ContainersDbContext>(sentinelTable: "containers");

        services.AddSingleton<IContainerRepository, Repository>();
        services.AddScoped<IContainerService, Service>();
        services.AddScoped<IAuthorizationHandler, ContainerAuthorizationHandler>();
    }
}
