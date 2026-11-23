using Humans.Base.Demo;

namespace Humans.Teams.Contracts;

/// <summary>
/// The departments Teams' demo story makes, for other sections' stories to build on. Read their ids with
/// <c>world.MadeAsync(TeamsDemo.TeamKeyPrefix)</c>.
/// </summary>
public static class TeamsDemo
{
    public static readonly DemoStoryKey StoryKey = new("teams");

    public const string TeamKeyPrefix = "teams:team:";

    /// <summary>Parents first. Names get a " (demo)" suffix so they never take a real team's name or slug.</summary>
    public static readonly IReadOnlyList<DemoTeam> Teams =
    [
        new("teams:team:gate", "Gate", null),
        new("teams:team:infrastructure", "Infrastructure", null),
        new("teams:team:participant-wellness", "Participant Wellness", null),
        new("teams:team:ice-and-water", "Ice and Water", null),
        new("teams:team:site-operations", "Site Operations", null),
        new("teams:team:production-logistics", "Production & Logistics", null),
        new("teams:team:werkhaus-barrio", "Werkhaus/Barrio", null),
        new("teams:team:power", "Power", null),
        new("teams:team:consent", "Consent", "teams:team:participant-wellness"),
        new("teams:team:welfare", "Welfare", "teams:team:participant-wellness"),
        new("teams:team:ohana-house", "Ohana House", "teams:team:participant-wellness"),
        new("teams:team:ice-ice-baby", "Ice Ice Baby", "teams:team:ice-and-water"),
        new("teams:team:icemakers", "Icemakers", "teams:team:ice-and-water"),
    ];
}

/// <summary>One demo department; <c>ParentKey</c> names its parent department, or is null at the top.</summary>
public sealed record DemoTeam(string Key, string Name, string? ParentKey);
