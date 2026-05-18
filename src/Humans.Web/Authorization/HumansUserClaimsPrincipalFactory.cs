using System.Security.Claims;
using Humans.Domain.Entities;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Humans.Web.Authorization;

/// <summary>
/// Replaces the default <c>Name</c> claim (which Identity sources from
/// <c>UserName</c>) with the user's <see cref="User.DisplayName"/>. After PR 1
/// of the email-identity-decoupling spec, <c>UserName</c> is the user's GUID
/// for any account created via the OAuth callback or magic-link signup, so
/// reading <c>User.Identity.Name</c> would otherwise show a GUID in the topbar
/// avatar and dashboard greeting. <c>NameIdentifier</c> still carries the
/// user id, so authorization is unaffected.
/// </summary>
public sealed class HumansUserClaimsPrincipalFactory : UserClaimsPrincipalFactory<User, IdentityRole<Guid>>
{
    public HumansUserClaimsPrincipalFactory(
        UserManager<User> userManager,
        RoleManager<IdentityRole<Guid>> roleManager,
        IOptions<IdentityOptions> options)
        : base(userManager, roleManager, options)
    {
    }

    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(User user)
    {
        var identity = await base.GenerateClaimsAsync(user);

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            var nameClaimType = Options.ClaimsIdentity.UserNameClaimType;
            var existing = identity.FindFirst(nameClaimType);
            if (existing is not null)
            {
                identity.RemoveClaim(existing);
            }
            identity.AddClaim(new Claim(nameClaimType, user.DisplayName));
        }

        return identity;
    }
}
