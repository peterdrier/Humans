namespace Humans.MailerLite;

/// <summary>MailerLite subscriber identity normalization shared by preview and synchronization.</summary>
internal static class MailerLiteEmailNormalization
{
    internal static string Normalize(string email) => email.Trim().ToLowerInvariant();
}
