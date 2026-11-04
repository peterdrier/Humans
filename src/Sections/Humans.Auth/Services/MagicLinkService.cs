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

    public async Task<bool> VerifyLoginTokenAsync(Guid userId, string token, CancellationToken ct = default)
    {
        if (await userService.GetRawUserInfoAsync(userId, ct) is null)
        {
            logger.LogWarning("Magic link login: user {UserId} not found", userId);
            return false;
        }

        var payload = urlBuilder.UnprotectLoginToken(token);
        if (payload is null)
        {
            logger.LogWarning("Magic link login: invalid or expired token for user {UserId}", userId);
            return false;
        }

        if (!string.Equals(payload, userId.ToString(), StringComparison.Ordinal))
        {
            logger.LogWarning("Magic link login: token userId mismatch for {UserId}", userId);
            return false;
        }

        if (!await rateLimiter.TryConsumeTokenAsync(token, TokenLifetime))
        {
            logger.LogWarning("Magic link login: token already used for user {UserId}", userId);
            return false;
        }

        return true;
    }

    public string? VerifySignupToken(string token, string? expectedEmail = null)
    {
        var payload = urlBuilder.UnprotectSignupToken(token);
        if (payload is null)
        {
            logger.LogWarning("Magic link signup: invalid or expired token for email {Email}",
                expectedEmail ?? "unknown");
        }

        return payload;
    }

    public async Task<string?> VerifyAndConsumeSignupTokenAsync(
        string token, string? expectedEmail = null, CancellationToken ct = default)
    {
        var payload = VerifySignupToken(token, expectedEmail);
        if (payload is null)
            return null;

        if (!await rateLimiter.TryConsumeTokenAsync(token, TokenLifetime))
        {
            logger.LogWarning("Magic link signup: token already used for email {Email}",
                expectedEmail ?? "unknown");
            return null;
        }

        return payload;
    }

    public void ReleaseSignupToken(string token) => rateLimiter.ReleaseTokenReservation(token);

    private async Task SendLoginLinkAsync(Guid userId, string sendToEmail, string? returnUrl, CancellationToken ct)
    {
        if (!await rateLimiter.TryReserveUserSendAsync(userId, RateLimitCooldown.ToTimeSpan()))
        {
            logger.LogWarning("Magic link rate-limited for user {UserId}", userId);
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
        if (!await rateLimiter.TryReserveSignupSendAsync(email, SignupCooldown))
        {
            logger.LogWarning("Magic link signup rate-limited for {Email}", email);
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
}
