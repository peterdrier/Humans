namespace Humans.Email.Contracts;

/// <summary>
/// Templates a member is waiting on before they can continue (magic link, verification, workspace credentials).
/// The send path drains these immediately and the batch query orders them first, so both read this one list.
/// </summary>
public static class TimeSensitiveTemplates
{
    public const string EmailVerification = "email_verification";
    public const string MagicLinkLogin = "magic_link_login";
    public const string MagicLinkSignup = "magic_link_signup";
    public const string WorkspaceCredentials = "workspace_credentials";

    /// <summary>An array so EF translates <c>Names.Contains(...)</c> into a SQL <c>IN</c> list.</summary>
    public static readonly string[] Names =
    [
        EmailVerification,
        MagicLinkLogin,
        MagicLinkSignup,
        WorkspaceCredentials,
    ];
}
