using Microsoft.AspNetCore.Identity;

namespace Humans.Users.Contracts;

/// <summary>
/// Shell's Identity builder delegates store registration to the Users section,
/// which keeps its persistence types internal.
/// </summary>
public static class UsersIdentityExtensions
{
    /// <summary>Points ASP.NET Identity's stores at the section's own <c>DbContext</c>.</summary>
    public static IdentityBuilder AddHumansEntityFrameworkStores(this IdentityBuilder builder) =>
        Section.AddIdentityStores(builder);
}
