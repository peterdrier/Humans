using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users.Contracts;

/// <summary>Registers ASP.NET Identity over the section's internal stores and claims factory.</summary>
public static class UsersIdentityExtensions
{
    public static IdentityBuilder AddHumansIdentity(this IServiceCollection services) =>
        Section.AddIdentity(services);
}
