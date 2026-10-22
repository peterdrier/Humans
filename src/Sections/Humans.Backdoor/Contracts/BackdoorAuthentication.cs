namespace Humans.Backdoor.Contracts;

/// <summary>Public so the Shell's onboarding gates can tell a machine request from a browsing session.</summary>
public static class BackdoorAuthentication
{
    public const string SchemeName = "BackdoorApiKey";
}
