namespace Humans.Email.Services;

/// <summary>
/// Addresses used by local and ticket-stub delivery paths, which must never reach SMTP.
/// </summary>
internal static class EmailTestAddress
{
    public static bool IsTestAddress(string email) =>
        email.EndsWith("@localhost", StringComparison.OrdinalIgnoreCase) ||
        email.EndsWith("@ticketstub.local", StringComparison.OrdinalIgnoreCase);
}
