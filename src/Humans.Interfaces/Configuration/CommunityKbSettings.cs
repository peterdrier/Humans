namespace Humans.Infrastructure.Configuration;

/// <summary>
/// Points the agent's community knowledge base at its own GitHub repo, kept separate from
/// the Humans code repo so content ships on its own cadence (not via the code prod-promotion
/// flow). Defaults to the public nobodies-collective/knowledge-base@main. The access token,
/// if any, falls back to GitHubSettings (the corpus is public, so usually none is needed).
/// </summary>
public sealed class CommunityKbSettings
{
    public const string SectionName = "CommunityKb";

    public string Owner { get; set; } = "nobodies-collective";
    public string Repository { get; set; } = "knowledge-base";
    public string Branch { get; set; } = "main";
    public string? AccessToken { get; set; }
}
