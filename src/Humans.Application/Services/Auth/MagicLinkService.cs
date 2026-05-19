using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Domain.Entities;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Auth;

public sealed class MagicLinkService(
    UserManager<User> userManager,
    IUserEmailService userEmailService,
    IUserService userService,
    IEmailService emailService,
    IMagicLinkUrlBuilder urlBuilder,
    IMagicLinkRateLimiter rateLimiter,
    IClock clock,
    ILogger<MagicLinkService> logger) : IMagicLinkService
{
    private static readonly Duration RateLimitCooldown = Duration.FromSeconds(60);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SignupCooldown = TimeSpan.FromSeconds(60);

    public async Task SendMagicLinkAsync(string email, string? returnUrl, CancellationToken ct = default)
    {
        var userEmail = await userEmailService.FindVerifiedEmailWithUserAsync(email, ct);
        if (userEmail is not null)
        {
            var ownerUser = await userManager.FindByIdAsync(userEmail.UserId.ToString());
            if (ownerUser is not null)
            {
                await SendLoginLinkAsync(ownerUser, userEmail.Email, returnUrl, ct);
                return;
            }
        }

        // No match — send signup link (with rate limiting).
        await SendSignupLinkAsync(email, returnUrl, ct);
    }

    public async Task<User?> VerifyLoginTokenAsync(Guid userId, string token, CancellationToken ct = default)
    {
        var user = await userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            logger.LogWarning("Magic link login: user {UserId} not found", userId);
            return null;
        }

        var payload = urlBuilder.UnprotectLoginToken(token);
        if (payload is null)
        {
            logger.LogInformation("Magic link login: invalid or expired token for user {UserId}", userId);
            return null;
        }

        if (!string.Equals(payload, userId.ToString(), StringComparison.Ordinal))
        {
            logger.LogWarning("Magic link login: token userId mismatch for {UserId}", userId);
            return null;
        }

        // Replay-prevention: consume token for its lifetime.
        if (!await rateLimiter.TryConsumeLoginTokenAsync(token, TokenLifetime))
        {
            logger.LogInformation("Magic link login: token already used for user {UserId}", userId);
            return null;
        }

        return user;
    }

    public string? VerifySignupToken(string token, string? expectedEmail = null)
    {
        var payload = urlBuilder.UnprotectSignupToken(token);
        if (payload is null)
        {
            logger.LogInformation("Magic link signup: invalid or expired token for email {Email}",
                expectedEmail ?? "unknown");
        }

        return payload;
    }

    public async Task<User?> FindUserByVerifiedEmailAsync(string email, CancellationToken ct = default)
    {
        var userEmail = await userEmailService.FindVerifiedEmailWithUserAsync(email, ct);
        if (userEmail is null)
            return null;

        return await userManager.FindByIdAsync(userEmail.UserId.ToString());
    }

    private async Task SendLoginLinkAsync(User user, string sendToEmail, string? returnUrl, CancellationToken ct)
    {
        // Rate limit: one magic link per 60 seconds per user
        var now = clock.GetCurrentInstant();
        if (user.MagicLinkSentAt is not null &&
            now - user.MagicLinkSentAt.Value < RateLimitCooldown)
        {
            logger.LogDebug("Magic link rate-limited for user {UserId}", user.Id);
            return; // Silently skip — same "check your email" message shown to user
        }

        var magicLinkUrl = urlBuilder.BuildLoginUrl(user.Id, returnUrl);

        var userInfo = await userService.GetUserInfoAsync(user.Id, ct);
        var displayName = string.IsNullOrWhiteSpace(userInfo?.BurnerName) ? sendToEmail : userInfo.BurnerName;

        await emailService.SendMagicLinkLoginAsync(
            sendToEmail, displayName, magicLinkUrl, ct: ct);

        user.MagicLinkSentAt = now;
        await userManager.UpdateAsync(user);

        logger.LogInformation("Magic link login sent to {Email} for user {UserId}", sendToEmail, user.Id);
    }

    private async Task SendSignupLinkAsync(string email, string? returnUrl, CancellationToken ct)
    {
        // Rate limit signup emails: one per 60 seconds per email address
        if (!await rateLimiter.TryReserveSignupSendAsync(email, SignupCooldown))
        {
            logger.LogDebug("Magic link signup rate-limited for {Email}", email);
            return;
        }

        try
        {
            var magicLinkUrl = urlBuilder.BuildSignupUrl(email, returnUrl);

            await emailService.SendMagicLinkSignupAsync(email, magicLinkUrl, ct: ct);

            logger.LogInformation("Magic link signup sent to {Email}", email);
        }
        catch
        {
            rateLimiter.ReleaseSignupReservation(email);
            throw;
        }
    }
}
