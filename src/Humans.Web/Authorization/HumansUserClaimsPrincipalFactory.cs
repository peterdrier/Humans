using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using Humans.Users.Contracts;

namespace Humans.Web.Authorization;

/// <summary>
/// Replaces the default <c>Name</c> claim (Identity sources it from <c>UserName</c>, which is the user's GUID for
/// OAuth and magic-link accounts) with the BurnerName, so the topbar never shows a GUID. <c>NameIdentifier</c> still
/// carries the user id, so authorization is unaffected.
/// </summary>
public sealed class HumansUserClaimsPrincipalFactory(
    UserManager<User> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> options,
    IUserService users)
    : UserClaimsPrincipalFactory<User, IdentityRole<Guid>>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var identity = await base.GenerateClaimsAsync(user);
        var info = await users.GetUserInfoAsync(user.Id);
        var burnerName = info?.BurnerName;

        if (!string.IsNullOrWhiteSpace(burnerName))
        {
            var nameClaimType = Options.ClaimsIdentity.UserNameClaimType;
            var existing = identity.FindFirst(nameClaimType);
            if (existing is not null)
            {
                identity.RemoveClaim(existing);
            }
            identity.AddClaim(new Claim(nameClaimType, burnerName));
        }

        return identity;
    }
}
