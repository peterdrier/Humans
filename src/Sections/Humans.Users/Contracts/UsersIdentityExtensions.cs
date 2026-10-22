using Microsoft.AspNetCore.Identity;

namespace Humans.Users.Contracts;

/// <summary>Lets Shell's Identity builder use the section's stores, which stay internal.</summary>
public static class UsersIdentityExtensions
{
    public static IdentityBuilder AddHumansEntityFrameworkStores(this IdentityBuilder builder) =>
        Section.AddIdentityStores(builder);
}
