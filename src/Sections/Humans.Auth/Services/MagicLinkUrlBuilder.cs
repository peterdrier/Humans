using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Base.Configuration;

namespace Humans.Auth.Services;

internal sealed class MagicLinkUrlBuilder(
    IDataProtectionProvider dataProtection,
    IOptions<EmailSettings> emailSettings,
    IClock clock) : IMagicLinkUrlBuilder
{
    private const string LoginProtectorPurpose = "MagicLinkLogin";
    private const string SignupProtectorPurpose = "MagicLinkSignup";
    private static readonly Duration TokenLifetime = Duration.FromMinutes(15);

    // ASP.NET's time-limited protector rejects expired and forged tokens with the same exception. This writes its
    // purpose and layout (8-byte big-endian UTC ticks, then the payload) by hand so the two can be told apart.
    // Changing either breaks every link still in someone's inbox.
    private const string TimeLimitedPurpose = "Microsoft.AspNetCore.DataProtection.TimeLimitedDataProtector.v1";

    private readonly IDataProtector _loginProtector = dataProtection.CreateProtector(LoginProtectorPurpose, TimeLimitedPurpose);
    private readonly IDataProtector _signupProtector = dataProtection.CreateProtector(SignupProtectorPurpose, TimeLimitedPurpose);
    private readonly string _baseUrl = emailSettings.Value.BaseUrl;

    public string BuildLoginUrl(Guid userId, string? returnUrl)
    {
        var token = Protect(_loginProtector, userId.ToString());
        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? string.Empty : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return $"{_baseUrl}/Account/MagicLinkConfirm?userId={userId}&token={encodedToken}{returnUrlParam}";
    }

    public MagicLinkToken? UnprotectLoginToken(string token) => UnprotectToken(_loginProtector, token);

    public string BuildSignupUrl(string email, string? returnUrl)
    {
        var token = Protect(_signupProtector, email);
        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? string.Empty : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        var encodedEmail = Uri.EscapeDataString(email);
        return $"{_baseUrl}/Account/MagicLinkSignup?token={encodedToken}&email={encodedEmail}{returnUrlParam}";
    }

    public MagicLinkToken? UnprotectSignupToken(string token) => UnprotectToken(_signupProtector, token);

    private string Protect(IDataProtector protector, string payload)
    {
        var expiresAt = clock.GetCurrentInstant() + TokenLifetime;
        var plaintext = new byte[8 + Encoding.UTF8.GetByteCount(payload)];
        BinaryPrimitives.WriteInt64BigEndian(plaintext, expiresAt.ToDateTimeUtc().Ticks);
        Encoding.UTF8.GetBytes(payload, plaintext.AsSpan(8));
        return WebEncoders.Base64UrlEncode(protector.Protect(plaintext));
    }

    private static MagicLinkToken? UnprotectToken(IDataProtector protector, string token)
    {
        if (string.IsNullOrEmpty(token)) return null;

        try
        {
            // Replay reservations hash the token text. Accept only the spelling
            // we issue, so equivalent Base64 encodings cannot redeem it twice.
            var protectedBytes = WebEncoders.Base64UrlDecode(token);
            if (!string.Equals(token, WebEncoders.Base64UrlEncode(protectedBytes), StringComparison.Ordinal))
                return null;

            var plaintext = protector.Unprotect(protectedBytes);
            if (plaintext.Length < 8) return null;

            var ticks = BinaryPrimitives.ReadInt64BigEndian(plaintext);
            var expiresAt = Instant.FromDateTimeUtc(new DateTime(ticks, DateTimeKind.Utc));
            return new MagicLinkToken(Encoding.UTF8.GetString(plaintext, 8, plaintext.Length - 8), expiresAt);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
