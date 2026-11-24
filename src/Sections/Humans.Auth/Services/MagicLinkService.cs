using NodaTime;
using Humans.Auth.Contracts;
using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.Auth.Services;

/// <summary>Login tokens bind to the requested identity, never its merge survivor.</summary>
internal sealed class MagicLinkService(
    IUserEmailService userEmailService,
    IUserService userService,
    IEmailService emailService,
    AuthEmails emailMessages,
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

    public async Task<MagicLinkFailure?> VerifyLoginTokenAsync(Guid userId, string token, CancellationToken ct = default)
    {
        if (await userService.GetRawUserInfoAsync(userId, ct) is null)
        {
            logger.LogWarning("Magic link login: user {UserId} not found", userId);
            return MagicLinkFailure.AccountNotFound;
        }

        var read = urlBuilder.UnprotectLoginToken(token);
        if (read.Token is not { } decoded)
        {
            logger.LogWarning("Magic link login: unreadable token for user {UserId}: {Problem}", userId, read.Problem);
            return MagicLinkFailure.Unreadable;
        }

        if (!string.Equals(decoded.Payload, userId.ToString(), StringComparison.Ordinal))
        {
            logger.LogWarning("Magic link login: token userId mismatch for {UserId}", userId);
            return MagicLinkFailure.Unreadable;
        }

        if (MinutesSinceExpiry(decoded) is { } minutesAgo)
        {
            logger.LogWarning("Magic link login: token for user {UserId} expired {MinutesAgo} minutes ago",
                userId, minutesAgo);
            return MagicLinkFailure.Expired;
        }

        if (!await rateLimiter.TryConsumeTokenAsync(token, TokenLifetime))
        {
            logger.LogWarning("Magic link login: token already used for user {UserId}", userId);
            return MagicLinkFailure.AlreadyUsed;
        }

        return null;
    }

    public MagicLinkSignupCheck VerifySignupToken(string token, string? expectedEmail = null)
    {
        var read = urlBuilder.UnprotectSignupToken(token);
        if (read.Token is not { } decoded)
        {
            logger.LogWarning("Magic link signup: unreadable token for email {Email}: {Problem}",
                expectedEmail ?? "unknown", read.Problem);
            return new(null, MagicLinkFailure.Unreadable);
        }

        if (MinutesSinceExpiry(decoded) is { } minutesAgo)
        {
            logger.LogWarning("Magic link signup: token for email {Email} expired {MinutesAgo} minutes ago",
                decoded.Payload, minutesAgo);
            return new(null, MagicLinkFailure.Expired);
        }

        return new(decoded.Payload, null);
    }

    private long? MinutesSinceExpiry(MagicLinkToken decoded)
    {
        var sinceExpiry = clock.GetCurrentInstant() - decoded.ExpiresAt;
        return sinceExpiry < Duration.Zero ? null : (long)sinceExpiry.TotalMinutes;
    }

    public async Task<MagicLinkSignupCheck> VerifyAndConsumeSignupTokenAsync(
        string token, string? expectedEmail = null, CancellationToken ct = default)
    {
        var check = VerifySignupToken(token, expectedEmail);
        if (check.Email is null)
            return check;

        if (!await rateLimiter.TryConsumeTokenAsync(token, TokenLifetime))
        {
            logger.LogWarning("Magic link signup: token already used for email {Email}",
                expectedEmail ?? "unknown");
            return new(null, MagicLinkFailure.AlreadyUsed);
        }

        return check;
    }

    public void ReleaseSignupToken(string token) => rateLimiter.ReleaseTokenReservation(token);

    private async Task SendLoginLinkAsync(Guid userId, string sendToEmail, string? returnUrl, CancellationToken ct)
    {
        var refused = await rateLimiter.ReserveUserSendAsync(userId, RateLimitCooldown.ToTimeSpan());
        if (refused > 0)
        {
            logger.LogWarning(
                "Magic link login rate-limited for {Email} (user {UserId}), {Count} times within {CooldownSeconds} seconds of the last link sent",
                sendToEmail, userId, refused, (int)RateLimitCooldown.TotalSeconds);
            return; // Same "check your email" message either way: no enumeration.
        }

        try
        {
            var magicLinkUrl = urlBuilder.BuildLoginUrl(userId, returnUrl);
            var userInfo = await userService.GetUserInfoAsync(userId, ct);
            var displayName = string.IsNullOrWhiteSpace(userInfo?.BurnerName) ? sendToEmail : userInfo.BurnerName;

            await emailService.SendAsync(
                emailMessages.MagicLinkLogin(userId, sendToEmail, displayName, magicLinkUrl, userInfo?.PreferredLanguage), ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to send magic link login for user {UserId}", userId);
            rateLimiter.ReleaseUserSendReservation(userId);
            throw;
        }

        logger.LogInformation("Magic link login sent to {Email} for user {UserId}", sendToEmail, userId);
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
            var magicLinkUrl = urlBuilder.BuildSignupUrl(email, returnUrl);

            await emailService.SendAsync(emailMessages.MagicLinkSignup(email, magicLinkUrl), ct);

            logger.LogInformation("Magic link signup sent to {Email}", email);
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
                "Magic link signup rate-limited for {Email} (no account has this address), {Count} times within {CooldownSeconds} seconds of the last link sent",
                email, refused, cooldownSeconds);
        }
        else
        {
            logger.LogWarning(
                "Magic link signup rate-limited for {Email} (unverified address of user {UserId}), {Count} times within {CooldownSeconds} seconds of the last link sent",
                email, owner.UserId, refused, cooldownSeconds);
        }
    }
}
