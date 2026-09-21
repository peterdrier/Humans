namespace Humans.Email.Contracts;

/// <summary>
/// The templates whose delivery is time-sensitive — a human is sitting in front
/// of a screen waiting for the mail to arrive before they can proceed (magic-link
/// login/signup, email verification, workspace credentials).
/// </summary>
/// <remarks>
/// Two behaviours key off this list, so it lives in one place and cannot drift
/// between them: <c>OutboxEmailService</c> triggers an immediate outbox drain for
/// these messages so the processor runs without waiting for the minute tick, and
/// <c>EmailOutboxRepository.GetProcessingBatchAsync</c> orders these rows ahead of
/// everything else so an immediate run does not drain a bulk backlog first
/// (nobodies-collective/Humans#1122). Public so a sending section that owns one of
/// these templates names the same constant the queue orders on
/// (peterdrier/Humans#1651).
/// </remarks>
public static class TimeSensitiveTemplates
{
    public const string EmailVerification = "email_verification";
    public const string MagicLinkLogin = "magic_link_login";
    public const string MagicLinkSignup = "magic_link_signup";
    public const string WorkspaceCredentials = "workspace_credentials";

    /// <summary>
    /// All time-sensitive template names. A static array so EF Core translates
    /// <c>Names.Contains(m.TemplateName)</c> into a SQL <c>IN</c> list.
    /// </summary>
    public static readonly string[] Names =
    [
        EmailVerification,
        MagicLinkLogin,
        MagicLinkSignup,
        WorkspaceCredentials,
    ];
}
