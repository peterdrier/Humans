namespace Humans.Base.Demo;

/// <summary>
/// The demo crowd Development's cast story makes, published here because every story draws on it and no section may
/// reference Development. Read the made humans with <c>world.MadeAsync(DemoCast.HumanKeyPrefix)</c>.
/// </summary>
public static class DemoCast
{
    public static readonly DemoStoryKey StoryKey = new("cast");

    public const string HumanKeyPrefix = "cast:human:";

    public const int Size = 200;

    public static string HumanKey(int n) => $"{HumanKeyPrefix}{n:D3}";

    /// <summary>
    /// Every 15th human skips the consents and every 25th stops before the profile; the rest are active volunteers
    /// a story can put on teams and shifts.
    /// </summary>
    public static bool IsActiveVolunteer(int n) => n % 15 != 0 && n % 25 != 0;
}
