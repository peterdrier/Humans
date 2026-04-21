using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;

namespace Humans.Application.Services.Auth;

/// <summary>
/// Application-layer implementation of <see cref="IMagicLinkService"/>.
/// MagicLinkService is special-cased under §15: it owns no tables (its only
/// persistent state is the per-user <c>MagicLinkSentAt</c> column on
/// <c>User</c>, updated via <see cref="UserManager{User}"/>). Data-protection
/// token generation/validation and URL construction are abstracted into
/// <see cref="IMagicLinkUrlBuilder"/> and rate-limit / replay state into
/// <see cref="IMagicLinkRateLimiter"/> — both Infrastructure concerns — so
/// this service has no <c>HumansDbContext</c>, <c>EmailSettings</c>, or
/// <c>IMemoryCache</c> dependency (same shape as
/// <c>CommunicationPreferenceService</c> + <c>IUnsubscribeTokenProvider</c>).
/// </summary>
/// <remarks>
/// Verified-email lookup goes through <see cref="IUserEmailService.FindVerifiedEmailWithUserAsync"/>
/// instead of raw <c>DbContext.UserEmails</c> queries.
/// </remarks>
public sealed class MagicLinkService : IMagicLinkService
{
    private static readonly Duration RateLimitCooldown = Duration.FromSeconds(60);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SignupCooldown = TimeSpan.FromSeconds(60);

    private readonly UserManager<User> _userManager;
    private readonly IUserEmailService _userEmailService;
    private readonly IEmailService _emailService;
    private readonly IMagicLinkUrlBuilder _urlBuilder;
    private readonly IMagicLinkRateLimiter _rateLimiter;
    private readonly IClock _clock;
    private readonly ILogger<MagicLinkService> _logger;

    public MagicLinkService(
        UserManager<User> userManager,
        IUserEmailService userEmailService,
        IEmailService emailService,
        IMagicLinkUrlBuilder urlBuilder,
        IMagicLinkRateLimiter rateLimiter,
        IClock clock,
        ILogger<MagicLinkService> logger)
    {
        _userManager = userManager;
        _userEmailService = userEmailService;
        _emailService = emailService;
        _urlBuilder = urlBuilder;
        _rateLimiter = rateLimiter;
        _clock = clock;
        _logger = logger;
    }

    public async Task SendMagicLinkAsync(string email, string? returnUrl, CancellationToken ct = default)
    {
        // 1. Look up by verified UserEmail first (supports all verified addresses)
        var userEmail = await _userEmailService.FindVerifiedEmailWithUserAsync(email, ct);

        if (userEmail is not null)
        {
            var ownerUser = await _userManager.FindByIdAsync(userEmail.UserId.ToString());
            if (ownerUser is not null)
            {
                await SendLoginLinkAsync(ownerUser, userEmail.Email, returnUrl, ct);
                return;
            }
        }

        // 2. Fallback: check User.NormalizedEmail (edge case: user exists but UserEmail row missing)
        var user = await _userManager.FindByEmailAsync(email);
        if (user is not null)
        {
            await SendLoginLinkAsync(user, user.Email!, returnUrl, ct);
            return;
        }

        // 3. No match — send signup link (with rate limiting)
        await SendSignupLinkAsync(email, returnUrl, ct);
    }

    public async Task<User?> VerifyLoginTokenAsync(Guid userId, string token, CancellationToken ct = default)
    {
        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            _logger.LogWarning("Magic link login: user {UserId} not found", userId);
            return null;
        }

        // Verify DataProtection token — payload is the user ID
        var payload = _urlBuilder.UnprotectLoginToken(token);
        if (payload is null)
        {
            _logger.LogInformation("Magic link login: invalid or expired token for user {UserId}", userId);
            return null;
        }

        if (!string.Equals(payload, userId.ToString(), StringComparison.Ordinal))
        {
            _logger.LogWarning("Magic link login: token userId mismatch for {UserId}", userId);
            return null;
        }

        // Mark token as consumed (cache for token lifetime to prevent replay)
        if (!await _rateLimiter.TryConsumeLoginTokenAsync(token, TokenLifetime))
        {
            _logger.LogInformation("Magic link login: token already used for user {UserId}", userId);
            return null;
        }

        return user;
    }

    public string? VerifySignupToken(string token, string? expectedEmail = null)
    {
        var payload = _urlBuilder.UnprotectSignupToken(token);
        if (payload is null)
        {
            _logger.LogInformation("Magic link signup: invalid or expired token for email {Email}",
                expectedEmail ?? "unknown");
        }

        return payload;
    }

    public async Task<User?> FindUserByVerifiedEmailAsync(string email, CancellationToken ct = default)
    {
        var userEmail = await _userEmailService.FindVerifiedEmailWithUserAsync(email, ct);
        if (userEmail is not null)
        {
            return await _userManager.FindByIdAsync(userEmail.UserId.ToString());
        }

        return await _userManager.FindByEmailAsync(email);
    }

    public async Task<User?> FindUserByAnyEmailAsync(string email, CancellationToken ct = default)
    {
        // 1. Check verified UserEmails first (strongest match)
        var verified = await FindUserByVerifiedEmailAsync(email, ct);
        if (verified is not null)
            return verified;

        // 2. Check User.Email on other accounts (the account's identity email).
        // We intentionally skip unverified UserEmail rows — those are in a "pending
        // verification/merge review" state and auto-linking would bypass the
        // AccountMergeRequest admin review gate.
        var normalizedEmail = _userManager.NormalizeEmail(email);
        return _userManager.Users
            .FirstOrDefault(u => u.NormalizedEmail == normalizedEmail);
    }

    private async Task SendLoginLinkAsync(User user, string sendToEmail, string? returnUrl, CancellationToken ct)
    {
        // Rate limit: one magic link per 60 seconds per user
        var now = _clock.GetCurrentInstant();
        if (user.MagicLinkSentAt is not null &&
            now - user.MagicLinkSentAt.Value < RateLimitCooldown)
        {
            _logger.LogDebug("Magic link rate-limited for user {UserId}", user.Id);
            return; // Silently skip — same "check your email" message shown to user
        }

        var magicLinkUrl = _urlBuilder.BuildLoginUrl(user.Id, returnUrl);

        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? sendToEmail : user.DisplayName;

        await _emailService.SendMagicLinkLoginAsync(
            sendToEmail, displayName, magicLinkUrl, ct: ct);

        user.MagicLinkSentAt = now;
        await _userManager.UpdateAsync(user);

        _logger.LogInformation("Magic link login sent to {Email} for user {UserId}", sendToEmail, user.Id);
    }

    private async Task SendSignupLinkAsync(string email, string? returnUrl, CancellationToken ct)
    {
        // Rate limit signup emails: one per 60 seconds per email address
        if (!await _rateLimiter.TryReserveSignupSendAsync(email, SignupCooldown))
        {
            _logger.LogDebug("Magic link signup rate-limited for {Email}", email);
            return;
        }

        try
        {
            var magicLinkUrl = _urlBuilder.BuildSignupUrl(email, returnUrl);

            await _emailService.SendMagicLinkSignupAsync(email, magicLinkUrl, ct: ct);

            _logger.LogInformation("Magic link signup sent to {Email}", email);
        }
        catch
        {
            _rateLimiter.ReleaseSignupReservation(email);
            throw;
        }
    }
}
