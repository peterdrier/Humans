namespace Humans.Backdoor.Contracts;

/// <summary>
/// The authentication scheme a Backdoor API key produces
/// (nobodies-collective/Humans#1128). Public so the Shell's onboarding gates can tell a
/// machine request apart from a browsing session — see
/// <c>Humans.Web.Authorization.MembershipRequiredFilter</c>.
/// </summary>
public static class BackdoorAuthentication
{
    /// <summary>The <c>AuthenticationType</c> stamped on the identity a key resolves to.</summary>
    public const string SchemeName = "BackdoorApiKey";
}
