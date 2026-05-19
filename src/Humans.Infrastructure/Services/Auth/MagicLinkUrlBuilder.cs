using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Humans.Infrastructure.Configuration;
using Humans.Application.Interfaces.Auth;

namespace Humans.Infrastructure.Services.Auth;

/// <summary>
/// Infrastructure implementation of <see cref="IMagicLinkUrlBuilder"/>. Uses
/// ASP.NET Core <see cref="IDataProtectionProvider"/> for token
/// protection/validation and <see cref="EmailSettings.BaseUrl"/> for URL
/// construction. Mirrors <c>UnsubscribeTokenProvider</c>.
/// </summary>
public sealed class MagicLinkUrlBuilder(
    IDataProtectionProvider dataProtection,
    IOptions<EmailSettings> emailSettings,
    ILogger<MagicLinkUrlBuilder> logger) : IMagicLinkUrlBuilder
{
    private const string LoginProtectorPurpose = "MagicLinkLogin";
    private const string SignupProtectorPurpose = "MagicLinkSignup";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(15);

    private readonly ITimeLimitedDataProtector _loginProtector = dataProtection.CreateProtector(LoginProtectorPurpose).ToTimeLimitedDataProtector();
    private readonly ITimeLimitedDataProtector _signupProtector = dataProtection.CreateProtector(SignupProtectorPurpose).ToTimeLimitedDataProtector();
    private readonly string _baseUrl = emailSettings.Value.BaseUrl;

    public string BuildLoginUrl(Guid userId, string? returnUrl)
    {
        var token = _loginProtector.Protect(userId.ToString(), TokenLifetime);
        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? string.Empty : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return $"{_baseUrl}/Account/MagicLinkConfirm?userId={userId}&token={encodedToken}{returnUrlParam}";
    }

    public string? UnprotectLoginToken(string token)
    {
        try
        {
            return _loginProtector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            logger.LogInformation("Magic link login: invalid or expired token");
            return null;
        }
    }

    public string BuildSignupUrl(string email, string? returnUrl)
    {
        var token = _signupProtector.Protect(email, TokenLifetime);
        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? string.Empty : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        var encodedEmail = Uri.EscapeDataString(email);
        return $"{_baseUrl}/Account/MagicLinkSignup?token={encodedToken}&email={encodedEmail}{returnUrlParam}";
    }

    public string? UnprotectSignupToken(string token)
    {
        try
        {
            return _signupProtector.Unprotect(token);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
