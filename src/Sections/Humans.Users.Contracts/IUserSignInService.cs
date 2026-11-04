using Microsoft.AspNetCore.Authentication;
using Humans.Base.Interfaces;
using Microsoft.AspNetCore.Identity;

namespace Humans.Users.Contracts;

/// <summary>
/// ASP.NET Identity's sign-in and credential calls, keyed by user id, so no caller outside Users loads the
/// <c>User</c> entity. Every sign-in is persistent: the 14-day sliding cookie depends on it.
/// </summary>
public interface IUserSignInService : IApplicationService
{
    /// <summary>Issues the auth cookie. False when no user row has this id.</summary>
    Task<bool> SignInAsync(Guid userId);

    Task SignOutAsync();

    /// <summary>Checks without signing in and without counting toward lockout. False when no user row has this id.</summary>
    Task<bool> CheckPasswordAsync(Guid userId, string password);

    Task<bool> HasPasswordAsync(Guid userId);

    /// <summary>
    /// Replaces the password, rotating the security stamp so existing sessions end, and turns lockout off. A rejected
    /// password keeps the old one. Loads the user in a fresh scope, so an entity another write in this request
    /// tracked cannot overwrite what that write saved.
    /// </summary>
    Task<IdentityResult> SetPasswordAsync(Guid userId, string password);

    AuthenticationProperties ConfigureExternalAuthenticationProperties(string provider, string? redirectUrl);

    /// <summary>Null when the external cookie carries no login.</summary>
    Task<ExternalLoginInfo?> GetExternalLoginInfoAsync();

    /// <summary>Signs in the account that already holds this provider login, bypassing two-factor.</summary>
    Task<SignInResult> ExternalLoginSignInAsync(ExternalLoginInfo info);
}
