using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Users.Services;

internal sealed class UserSignInService(
    SignInManager<User> signInManager,
    UserManager<User> userManager,
    IServiceScopeFactory scopeFactory) : IUserSignInService
{
    public async Task<bool> SignInAsync(Guid userId)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
            return false;

        await signInManager.SignInAsync(user, isPersistent: true);
        return true;
    }

    public Task SignOutAsync() => signInManager.SignOutAsync();

    public async Task<bool> CheckPasswordAsync(Guid userId, string password)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is not null
            && (await signInManager.CheckPasswordSignInAsync(user, password, lockoutOnFailure: false)).Succeeded;
    }

    public async Task<bool> HasPasswordAsync(Guid userId)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        return user is not null && await userManager.HasPasswordAsync(user);
    }

    public async Task<IdentityResult> SetPasswordAsync(Guid userId, string password)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = await manager.FindByIdAsync(userId.ToString());
        if (user is null)
            return IdentityResult.Failed(new IdentityError { Description = "Account not found." });

        // ResetPasswordAsync validates before replacing the hash, so a rejected password keeps the old one.
        var token = await manager.GeneratePasswordResetTokenAsync(user);
        var reset = await manager.ResetPasswordAsync(user, token, password);
        if (reset.Succeeded)
            await manager.SetLockoutEnabledAsync(user, false);
        return reset;
    }

    public AuthenticationProperties ConfigureExternalAuthenticationProperties(string provider, string? redirectUrl) =>
        signInManager.ConfigureExternalAuthenticationProperties(provider, redirectUrl);

    public Task<ExternalLoginInfo?> GetExternalLoginInfoAsync() => signInManager.GetExternalLoginInfoAsync();

    public Task<SignInResult> ExternalLoginSignInAsync(ExternalLoginInfo info) =>
        signInManager.ExternalLoginSignInAsync(info.LoginProvider, info.ProviderKey, isPersistent: true,
            bypassTwoFactor: true);
}
