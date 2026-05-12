using System.Security.Cryptography;
using System.Text;

namespace Humans.Application.Services.Mailer;

/// <summary>
/// SHA-256 hash of a normalized email. Used by Mailer's forgotten-emails
/// skip-list so we can suppress GDPR-deleted users without storing their
/// plaintext addresses. Normalization mirrors the existing email-equality
/// rules in <c>UserEmailService</c> (case-insensitive, trim,
/// gmail/googlemail aliasing).
/// </summary>
public static class EmailHasher
{
    public static string Hash(string email)
    {
        var normalized = NormalizeForComparison(email);
        var bytes = Encoding.UTF8.GetBytes(normalized);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NormalizeForComparison(string email)
    {
        var trimmed = (email ?? string.Empty).Trim().ToLowerInvariant();
        var at = trimmed.IndexOf('@');
        if (at < 0) return trimmed;
        var local = trimmed[..at];
        var domain = trimmed[(at + 1)..];
        if (domain is "googlemail.com") domain = "gmail.com";
        return $"{local}@{domain}";
    }
}
