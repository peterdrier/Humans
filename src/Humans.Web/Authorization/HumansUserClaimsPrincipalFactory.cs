using System.Security.Claims;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Humans.Web.Authorization;

/// <summary>
/// Replaces the default <c>Name</c> claim (which Identity sources from
/// <c>UserName</c>) with the user's resolved BurnerName. After PR 1
/// of the email-identity-decoupling spec, <c>UserName</c> is the user's GUID
/// for any account created via the OAuth callback or magic-link signup, so
/// reading <c>User.Identity.Name</c> would otherwise show a GUID in the topbar
/// avatar and dashboard greeting. <c>NameIdentifier</c> still carries the
/// user id, so authorization is unaffected.
/// </summary>
public sealed class HumansUserClaimsPrincipalFactory(
    UserManager<User> userManager,
    RoleManager<IdentityRole<Guid>> roleManager,
    IOptions<IdentityOptions> options,
    IUserServiceRead users)
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
