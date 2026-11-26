using NodaTime;
using Humans.Auth.Contracts;
using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.Auth.Services;

/// <summary>Login tokens bind to the requested identity, never its merge survivor.</summary>
internal sealed class SignInLinkService(
    IUserEmailService userEmailService,
    IUserService userService,
    IEmailService emailService,
    AuthEmails emailMessages,
    ISignInLinkUrlBuilder urlBuilder,
    ISignInLinkRateLimiter rateLimiter,
    IClock clock,
    ILogger<SignInLinkService> logger) : ISignInLinkService
{
    private static readonly Duration RateLimitCooldown = Duration.FromSeconds(60);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SignupCooldown = TimeSpan.FromSeconds(60);

    public async Task SendSignInLinkAsync(string email, string? returnUrl, CancellationToken ct = default)
    {
        var userEmail = (await userEmailService.FindByAddressAsync(email, aliased: true, verifiedOnly: true, ct))
            .FirstOrDefault();
        if (userEmail is not null)
        {
            // The row's foreign key guarantees the owner exists.
            await SendLoginLinkAsync(userEmail.UserId, userEmail.Email, returnUrl, ct);
            return;
        }

        await SendSignupLinkAsync(email, returnUrl, ct);
    }

    public async Task<bool> VerifySignInTokenAsync(Guid userId, string token, CancellationToken ct = default)
    {
        if (await userService.GetRawUserInfoAsync(userId, ct) is null)
        {
            logger.LogWarning("Sign-in link: user {UserId} not found", userId);
            return false;
        }

        var decoded = urlBuilder.UnprotectSignInToken(token);
        if (decoded is null)
        {
            logger.LogWarning(
                "Sign-in link: unreadable token for user {UserId} (malformed, tampered, or signed with a retired key)",
                userId);
            return false;
        }

        if (!string.Equals(decoded.Payload, userId.ToString(), StringComparison.Ordinal))
        {
            logger.LogWarning("Sign-in link: token userId mismatch for {UserId}", userId);
            return false;
        }

        if (MinutesSinceExpiry(decoded) is { } minutesAgo)
        {
            logger.LogWarning("Sign-in link: token for user {UserId} expired {MinutesAgo} minutes ago",
                userId, minutesAgo);
            return false;
        }

        if (!await rateLimiter.TryConsumeTokenAsync(token, TokenLifetime))
        {
            logger.LogWarning("Sign-in link: token already used for user {UserId}", userId);
            return false;
        }

        return true;
    }

    public string? VerifySignupToken(string token, string? expectedEmail = null)
    {
        var decoded = urlBuilder.UnprotectSignupToken(token);
        if (decoded is null)
        {
            logger.LogWarning(
                "Sign-in link signup: unreadable token for email {Email} (malformed, tampered, or signed with a retired key)",
                expectedEmail ?? "unknown");
            return null;
        }

        if (MinutesSinceExpiry(decoded) is { } minutesAgo)
        {
            logger.LogWarning("Sign-in link signup: token for email {Email} expired {MinutesAgo} minutes ago",
                decoded.Payload, minutesAgo);
            return null;
        }

        return decoded.Payload;
    }

    private long? MinutesSinceExpiry(SignInLinkToken decoded)
    {
        var sinceExpiry = clock.GetCurrentInstant() - decoded.ExpiresAt;
        return sinceExpiry < Duration.Zero ? null : (long)sinceExpiry.TotalMinutes;
    }

    public async Task<string?> ConsumeSignupTokenAsync(
        string token, string? expectedEmail = null, CancellationToken ct = default)
    {
        var payload = VerifySignupToken(token, expectedEmail);
        if (payload is null)
            return null;

        if (!await rateLimiter.TryConsumeTokenAsync(token, TokenLifetime))
        {
            logger.LogWarning("Sign-in link signup: token already used for email {Email}",
                expectedEmail ?? "unknown");
            return null;
        }

        return payload;
    }

    public void ReleaseSignupToken(string token) => rateLimiter.ReleaseTokenReservation(token);

    private async Task SendLoginLinkAsync(Guid userId, string sendToEmail, string? returnUrl, CancellationToken ct)
    {
        var refused = await rateLimiter.ReserveUserSendAsync(userId, RateLimitCooldown.ToTimeSpan());
        if (refused > 0)
        {
            logger.LogWarning(
                "Sign-in link rate-limited for {Email} (user {UserId}), {Count} times within {CooldownSeconds} seconds of the last link sent",
                sendToEmail, userId, refused, (int)RateLimitCooldown.TotalSeconds);
            return; // Same "check your email" message either way: no enumeration.
        }

        try
        {
            var signInLinkUrl = urlBuilder.BuildSignInUrl(userId, returnUrl);
            var userInfo = await userService.GetUserInfoAsync(userId, ct);
            var displayName = string.IsNullOrWhiteSpace(userInfo?.BurnerName) ? sendToEmail : userInfo.BurnerName;

            await emailService.SendAsync(
                emailMessages.SignInLink(userId, sendToEmail, displayName, signInLinkUrl, userInfo?.PreferredLanguage), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send sign-in link for user {UserId}", userId);
            rateLimiter.ReleaseUserSendReservation(userId);
            throw;
        }

        logger.LogInformation("Sign-in link sent to {Email} for user {UserId}", sendToEmail, userId);
    }

    private async Task SendSignupLinkAsync(string email, string? returnUrl, CancellationToken ct)
    {
        var refused = await rateLimiter.ReserveSignupSendAsync(email, SignupCooldown);
        if (refused > 0)
        {
            await LogSignupRateLimitedAsync(email, refused, ct);
            return;
        }

        try
        {
            var signInLinkUrl = urlBuilder.BuildSignupUrl(email, returnUrl);

            await emailService.SendAsync(emailMessages.SignInLinkSignup(email, signInLinkUrl), ct);

            logger.LogInformation("Sign-in link signup sent to {Email}", email);
        }
        catch
        {
            rateLimiter.ReleaseSignupReservation(email);
            throw;
        }
    }

    /// <summary>
    /// A verified address takes the login path, so a signup can only match an account through an unverified one. That
    /// match is worth knowing: someone may be trying to get into an account that already exists.
    /// </summary>
    private async Task LogSignupRateLimitedAsync(string email, int refused, CancellationToken ct)
    {
        var owner = (await userEmailService.FindByAddressAsync(email, aliased: true, verifiedOnly: false, ct))
            .FirstOrDefault();
        var cooldownSeconds = (int)SignupCooldown.TotalSeconds;
        if (owner is null)
        {
            logger.LogWarning(
                "Sign-in link signup rate-limited for {Email} (no account has this address), {Count} times within {CooldownSeconds} seconds of the last link sent",
                email, refused, cooldownSeconds);
        }
        else
        {
            logger.LogWarning(
                "Sign-in link signup rate-limited for {Email} (unverified address of user {UserId}), {Count} times within {CooldownSeconds} seconds of the last link sent",
                email, owner.UserId, refused, cooldownSeconds);
        }
    }
}
