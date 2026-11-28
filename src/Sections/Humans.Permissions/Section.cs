using Humans.Base.Interfaces;
using Humans.Permissions.Authorization;
using Humans.Permissions.Contracts;
using Humans.Permissions.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Permissions;

public sealed class Section : SectionBase
{
    public override void Register(IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<IPermissionService, PermissionService>();
        services.AddScoped<IAuthorizationHandler, PermissionHandler>();
    }
}
