using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Base.Configuration;

namespace Humans.Auth.Services;

internal sealed class SignInLinkUrlBuilder(
    IDataProtectionProvider dataProtection,
    IOptions<EmailSettings> emailSettings,
    IClock clock) : ISignInLinkUrlBuilder
{
    // Data-protection purposes: changing one voids every link already sent, so they keep their original names.
    private const string SignInProtectorPurpose = "MagicLinkLogin";
    private const string SignupProtectorPurpose = "MagicLinkSignup";
    private static readonly Duration TokenLifetime = Duration.FromMinutes(15);

    // ASP.NET's time-limited protector rejects expired and forged tokens with the same exception. This writes its
    // purpose and layout (8-byte big-endian UTC ticks, then the payload) by hand so the two can be told apart.
    // Changing either breaks every link still in someone's inbox.
    private const string TimeLimitedPurpose = "Microsoft.AspNetCore.DataProtection.TimeLimitedDataProtector.v1";

    private readonly IDataProtector _signInProtector = dataProtection.CreateProtector(SignInProtectorPurpose, TimeLimitedPurpose);
    private readonly IDataProtector _signupProtector = dataProtection.CreateProtector(SignupProtectorPurpose, TimeLimitedPurpose);
    private readonly string _baseUrl = emailSettings.Value.BaseUrl;

    public string BuildSignInUrl(Guid userId, string? returnUrl)
    {
        var token = Protect(_signInProtector, userId.ToString());
        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? string.Empty : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        return $"{_baseUrl}/Account/SignInLinkConfirm?userId={userId}&token={encodedToken}{returnUrlParam}";
    }

    public SignInLinkToken? UnprotectSignInToken(string token) => UnprotectToken(_signInProtector, token);

    public string BuildSignupUrl(string email, string? returnUrl)
    {
        var token = Protect(_signupProtector, email);
        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? string.Empty : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        var encodedEmail = Uri.EscapeDataString(email);
        return $"{_baseUrl}/Account/SignInLinkSignup?token={encodedToken}&email={encodedEmail}{returnUrlParam}";
    }

    public SignInLinkToken? UnprotectSignupToken(string token) => UnprotectToken(_signupProtector, token);

    private string Protect(IDataProtector protector, string payload)
    {
        var expiresAt = clock.GetCurrentInstant() + TokenLifetime;
        var plaintext = new byte[8 + Encoding.UTF8.GetByteCount(payload)];
        BinaryPrimitives.WriteInt64BigEndian(plaintext, expiresAt.ToDateTimeUtc().Ticks);
        Encoding.UTF8.GetBytes(payload, plaintext.AsSpan(8));
        return WebEncoders.Base64UrlEncode(protector.Protect(plaintext));
    }

    private static SignInLinkToken? UnprotectToken(IDataProtector protector, string token)
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
            return new SignInLinkToken(Encoding.UTF8.GetString(plaintext, 8, plaintext.Length - 8), expiresAt);
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }
}
