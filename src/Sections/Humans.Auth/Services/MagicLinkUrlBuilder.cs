using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.Base.Configuration;

namespace Humans.Auth.Services;

internal sealed class MagicLinkUrlBuilder(
    IDataProtectionProvider dataProtection,
    IKeyManager keyManager,
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

    // A Data Protection payload starts with this magic number, then the id of the key that protected it.
    private const uint PayloadMagic = 0x09F0C9F0;
    private const int PayloadHeaderLength = 4 + 16;

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

    public MagicLinkRead UnprotectLoginToken(string token) => UnprotectToken(_loginProtector, token, _signupProtector, "signup");

    public string BuildSignupUrl(string email, string? returnUrl)
    {
        var token = Protect(_signupProtector, email);
        var encodedToken = Uri.EscapeDataString(token);
        var returnUrlParam = string.IsNullOrEmpty(returnUrl) ? string.Empty : $"&returnUrl={Uri.EscapeDataString(returnUrl)}";
        var encodedEmail = Uri.EscapeDataString(email);
        return $"{_baseUrl}/Account/MagicLinkSignup?token={encodedToken}&email={encodedEmail}{returnUrlParam}";
    }

    public MagicLinkRead UnprotectSignupToken(string token) => UnprotectToken(_signupProtector, token, _loginProtector, "login");

    private string Protect(IDataProtector protector, string payload)
    {
        var expiresAt = clock.GetCurrentInstant() + TokenLifetime;
        var plaintext = new byte[8 + Encoding.UTF8.GetByteCount(payload)];
        BinaryPrimitives.WriteInt64BigEndian(plaintext, expiresAt.ToDateTimeUtc().Ticks);
        Encoding.UTF8.GetBytes(payload, plaintext.AsSpan(8));
        return WebEncoders.Base64UrlEncode(protector.Protect(plaintext));
    }

    private MagicLinkRead UnprotectToken(IDataProtector protector, string token, IDataProtector otherKind, string otherKindName)
    {
        if (string.IsNullOrEmpty(token)) return MagicLinkRead.Unreadable("token is empty");

        byte[] protectedBytes;
        try
        {
            protectedBytes = WebEncoders.Base64UrlDecode(token);
        }
        catch (FormatException)
        {
            return MagicLinkRead.Unreadable("token is not Base64url");
        }

        // Replay reservations hash the token text. Accept only the spelling
        // we issue, so equivalent Base64 encodings cannot redeem it twice.
        if (!string.Equals(token, WebEncoders.Base64UrlEncode(protectedBytes), StringComparison.Ordinal))
            return MagicLinkRead.Unreadable("token is not spelled the way it was issued");

        byte[] plaintext;
        try
        {
            plaintext = protector.Unprotect(protectedBytes);
        }
        catch (CryptographicException)
        {
            return MagicLinkRead.Unreadable(WhyUnprotectFailed(protectedBytes, otherKind, otherKindName));
        }

        if (plaintext.Length < 8) return MagicLinkRead.Unreadable("token is too short to hold an expiry");

        var ticks = BinaryPrimitives.ReadInt64BigEndian(plaintext);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
            return MagicLinkRead.Unreadable("token's expiry is not a valid date");

        var expiresAt = Instant.FromDateTimeUtc(new DateTime(ticks, DateTimeKind.Utc));
        return new MagicLinkRead(new MagicLinkToken(Encoding.UTF8.GetString(plaintext, 8, plaintext.Length - 8), expiresAt), null);
    }

    private string WhyUnprotectFailed(byte[] protectedBytes, IDataProtector otherKind, string otherKindName)
    {
        if (protectedBytes.Length < PayloadHeaderLength
            || BinaryPrimitives.ReadUInt32BigEndian(protectedBytes) != PayloadMagic)
            return "token is not a Data Protection payload";

        var keyId = new Guid(protectedBytes.AsSpan(4, 16));
        if (!keyManager.GetAllKeys().Any(k => k.KeyId == keyId))
            return $"token was signed with key {keyId}, which is not in the key ring";

        try
        {
            otherKind.Unprotect(protectedBytes);
            return $"token is a {otherKindName} link's";
        }
        catch (CryptographicException)
        {
            return "token fails its integrity check: it was altered";
        }
    }
}
