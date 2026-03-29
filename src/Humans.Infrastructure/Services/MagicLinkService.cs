using System.Security.Cryptography;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class MagicLinkService : IMagicLinkService
{
    private const string LoginProtectorPurpose = "MagicLinkLogin";
    private const string SignupProtectorPurpose = "MagicLinkSignup";
    private static readonly Duration RateLimitCooldown = Duration.FromSeconds(60);
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IEmailService _emailService;
    private readonly ITimeLimitedDataProtector _loginProtector;
    private readonly ITimeLimitedDataProtector _signupProtector;
    private readonly IMemoryCache _memoryCache;
    private readonly IClock _clock;
    private readonly ILogger<MagicLinkService> _logger;
    private readonly EmailSettings _emailSettings;

    public MagicLinkService(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IEmailService emailService,
        IDataProtectionProvider dataProtectionProvider,
        IMemoryCache memoryCache,
        IClock clock,
        IOptions<EmailSettings> emailSettings,
        ILogger<MagicLinkService> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _emailService = emailService;
        _loginProtector = dataProtectionProvider
            .CreateProtector(LoginProtectorPurpose)
            .ToTimeLimitedDataProtector();
        _signupProtector = dataProtectionProvider
            .CreateProtector(SignupProtectorPurpose)
            .ToTimeLimitedDataProtector();
        _memoryCache = memoryCache;
        _clock = clock;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    public async Task SendMagicLinkAsync(string email, string? returnUrl, CancellationToken ct = default)
    {
        // 1. Look up by verified UserEmail first (supports all verified addresses)
#pragma warning disable MA0011, RCS1155 // EF Core can only translate parameterless ToUpper()
        var userEmail = await _dbContext.UserEmails
            .Include(ue => ue.User)
            .FirstOrDefaultAsync(ue => ue.IsVerified &&
                ue.Email.ToUpper() == email.ToUpper(), ct);
#pragma warning restore MA0011, RCS1155

        if (userEmail is not null)
        {
            await SendLoginLinkAsync(userEmail.User, userEmail.Email, returnUrl, ct);
            return;
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
        // Check if this token was already consumed
        var cacheKey = $"magic_link_used:{token[..Math.Min(token.Length, 32)]}";
        if (_memoryCache.TryGetValue(cacheKey, out _))
        {
            _logger.LogWarning("Magic link login: token already used for user {UserId}", userId);
            return null;
        }

        var user = await _userManager.FindByIdAsync(userId.ToString());
        if (user is null)
        {
            _logger.LogWarning("Magic link login: user {UserId} not found", userId);
            return null;
        }

        // Verify DataProtection token — payload is the user ID
        string? payload;
        try
        {
            payload = _loginProtector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            _logger.LogWarning("Magic link login: invalid or expired token for user {UserId}", userId);
            return null;
        }

        if (!string.Equals(payload, userId.ToString(), StringComparison.Ordinal))
        {
            _logger.LogWarning("Magic link login: token userId mismatch for {UserId}", userId);
            return null;
        }

        // Mark token as consumed (cache for token lifetime to prevent replay)
        _memoryCache.Set(cacheKey, true, TokenLifetime);

        return user;
    }

    public string? VerifySignupToken(string token)
    {
        try
        {
            return _signupProtector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            _logger.LogWarning("Magic link signup: invalid or expired token");
            return null;
        }
    }

    public async Task<User?> FindUserByVerifiedEmailAsync(string email, CancellationToken ct = default)
    {
#pragma warning disable MA0011, RCS1155 // EF Core can only translate parameterless ToUpper()
        var userEmail = await _dbContext.UserEmails
            .Include(ue => ue.User)
            .FirstOrDefaultAsync(ue => ue.IsVerified &&
                ue.Email.ToUpper() == email.ToUpper(), ct);
#pragma warning restore MA0011, RCS1155

        if (userEmail is not null)
        {
            return userEmail.User;
        }

        return await _userManager.FindByEmailAsync(email);
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

        // DataProtection token with user ID as payload, 15-minute expiry
        var token = _loginProtector.Protect(user.Id.ToString(), TokenLifetime);

        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        var magicLinkUrl = $"{_emailSettings.BaseUrl}/Account/MagicLinkConfirm?userId={user.Id}&token={encodedToken}{returnUrlParam}";

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
        var cacheKey = $"magic_link_signup:{email.ToUpperInvariant()}";
        if (_memoryCache.TryGetValue(cacheKey, out _))
        {
            _logger.LogDebug("Magic link signup rate-limited for {Email}", email);
            return;
        }

        var token = _signupProtector.Protect(email, TokenLifetime);
        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        var magicLinkUrl = $"{_emailSettings.BaseUrl}/Account/MagicLinkSignup?token={encodedToken}{returnUrlParam}";

        await _emailService.SendMagicLinkSignupAsync(email, magicLinkUrl, ct: ct);

        _memoryCache.Set(cacheKey, true, TimeSpan.FromSeconds(60));

        _logger.LogInformation("Magic link signup sent to {Email}", email);
    }
}
