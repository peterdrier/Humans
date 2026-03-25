using System.Security.Cryptography;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Data;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NodaTime;

namespace Humans.Infrastructure.Services;

public class MagicLinkService : IMagicLinkService
{
    private const string LoginTokenPurpose = "MagicLinkLogin";
    private const string SignupProtectorPurpose = "MagicLinkSignup";
    private static readonly Duration RateLimitCooldown = Duration.FromSeconds(60);
    private static readonly TimeSpan SignupTokenLifetime = TimeSpan.FromMinutes(15);

    private readonly HumansDbContext _dbContext;
    private readonly UserManager<User> _userManager;
    private readonly IEmailService _emailService;
    private readonly IEmailRenderer _renderer;
    private readonly ITimeLimitedDataProtector _signupProtector;
    private readonly IClock _clock;
    private readonly ILogger<MagicLinkService> _logger;
    private readonly EmailSettings _emailSettings;

    public MagicLinkService(
        HumansDbContext dbContext,
        UserManager<User> userManager,
        IEmailService emailService,
        IEmailRenderer renderer,
        IDataProtectionProvider dataProtectionProvider,
        IClock clock,
        IOptions<EmailSettings> emailSettings,
        ILogger<MagicLinkService> logger)
    {
        _dbContext = dbContext;
        _userManager = userManager;
        _emailService = emailService;
        _renderer = renderer;
        _signupProtector = dataProtectionProvider
            .CreateProtector(SignupProtectorPurpose)
            .ToTimeLimitedDataProtector();
        _clock = clock;
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    public async Task SendMagicLinkAsync(string email, string? returnUrl, CancellationToken ct = default)
    {
        // 1. Look up by verified UserEmail first (supports all verified addresses)
        var normalizedEmail = email.ToUpperInvariant();
        var userEmail = await _dbContext.UserEmails
            .Include(ue => ue.User)
            .FirstOrDefaultAsync(ue => ue.IsVerified &&
                ue.Email.ToUpperInvariant() == normalizedEmail, ct);

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

        // 3. No match — send signup link
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

        var isValid = await _userManager.VerifyUserTokenAsync(
            user, TokenOptions.DefaultEmailProvider, LoginTokenPurpose, token);

        if (!isValid)
        {
            _logger.LogWarning("Magic link login: invalid or expired token for user {UserId}", userId);
            return null;
        }

        // Rotate security stamp to invalidate this and any other outstanding tokens
        await _userManager.UpdateSecurityStampAsync(user);

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

        var token = await _userManager.GenerateUserTokenAsync(
            user, TokenOptions.DefaultEmailProvider, LoginTokenPurpose);

        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        var magicLinkUrl = $"{_emailSettings.BaseUrl}/Account/MagicLink?userId={user.Id}&token={encodedToken}{returnUrlParam}";

        var displayName = string.IsNullOrWhiteSpace(user.DisplayName) ? sendToEmail : user.DisplayName;

        await _emailService.SendMagicLinkLoginAsync(
            sendToEmail, displayName, magicLinkUrl, ct: ct);

        user.MagicLinkSentAt = now;
        await _userManager.UpdateAsync(user);

        _logger.LogInformation("Magic link login sent to {Email} for user {UserId}", sendToEmail, user.Id);
    }

    private async Task SendSignupLinkAsync(string email, string? returnUrl, CancellationToken ct)
    {
        var token = _signupProtector.Protect(email, SignupTokenLifetime);
        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? "" : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        var magicLinkUrl = $"{_emailSettings.BaseUrl}/Account/MagicLinkSignup?token={encodedToken}{returnUrlParam}";

        await _emailService.SendMagicLinkSignupAsync(email, magicLinkUrl, ct: ct);

        _logger.LogInformation("Magic link signup sent to {Email}", email);
    }
}
