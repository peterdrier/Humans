using Humans.Users.Contracts;

namespace Humans.Users.Services;

internal static class PreferredLanguageCalculator
{
    public static IReadOnlyList<PreferredLanguageCount> Build(
        IReadOnlyCollection<UserInfo> snapshot) =>
        Counted(snapshot)
            .GroupBy(u => u.PreferredLanguage, StringComparer.Ordinal)
            .Select(g => new PreferredLanguageCount(g.Key, g.Count()))
            .ToList();

    public static HighContrastCount CountHighContrast(IReadOnlyCollection<UserInfo> snapshot)
    {
        var counted = Counted(snapshot).ToList();
        return new HighContrastCount(
            On: counted.Count(u => u.HighContrast == true),
            Off: counted.Count(u => u.HighContrast == false),
            Device: counted.Count(u => u.HighContrast is null));
    }

    private static IEnumerable<UserInfo> Counted(IReadOnlyCollection<UserInfo> snapshot) =>
        snapshot.Where(u => u.State == UserState.Active && u.Profile is not null && u.DeletionRequestedAt is null);
}

internal sealed record PreferredLanguageCount(string Language, int Count);

/// <summary>Members who chose high contrast, chose it off, or left it to their device.</summary>
internal sealed record HighContrastCount(int On, int Off, int Device);
