using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using Humans.Base.Configuration;

namespace Humans.Auth.Services;

/// <summary>
/// Implementation of <see cref="IMagicLinkUrlBuilder"/>. Uses
/// ASP.NET Core <see cref="IDataProtectionProvider"/> for token
/// protection/validation and <see cref="EmailSettings.BaseUrl"/> for URL
/// construction.
/// </summary>
internal sealed class MagicLinkUrlBuilder(
    IDataProtectionProvider dataProtection,
    IOptions<EmailSettings> emailSettings) : IMagicLinkUrlBuilder
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

    public string? UnprotectLoginToken(string token) => UnprotectToken(_loginProtector, token);

    public string BuildSignupUrl(string email, string? returnUrl)
    {
        var token = _signupProtector.Protect(email, TokenLifetime);
        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? string.Empty : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        var encodedEmail = Uri.EscapeDataString(email);
        return $"{_baseUrl}/Account/MagicLinkSignup?token={encodedToken}&email={encodedEmail}{returnUrlParam}";
    }

    public string? UnprotectSignupToken(string token) => UnprotectToken(_signupProtector, token);

    private static string? UnprotectToken(ITimeLimitedDataProtector protector, string token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            // Replay reservations hash the token text. Accept only the spelling
            // we issue, so equivalent Base64 encodings cannot redeem it twice.
            if (!string.Equals(token, WebEncoders.Base64UrlEncode(WebEncoders.Base64UrlDecode(token)),
                    StringComparison.Ordinal))
                return null;

            return protector.Unprotect(token);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}
