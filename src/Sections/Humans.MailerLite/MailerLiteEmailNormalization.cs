namespace Humans.MailerLite;

/// <summary>Shared by preview and sync, so both match subscribers the same way.</summary>
internal static class MailerLiteEmailNormalization
{
    internal static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
