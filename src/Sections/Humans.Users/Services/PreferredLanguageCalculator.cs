using Humans.Users.Contracts;

namespace Humans.Users.Services;

internal static class PreferredLanguageCalculator
{
    public static IReadOnlyList<PreferredLanguageCount> Build(
        IReadOnlyCollection<UserInfo> snapshot) =>
        snapshot
            .Where(u => u.State == UserState.Active && u.Profile is not null && u.DeletionRequestedAt is null)
            .GroupBy(u => u.PreferredLanguage, StringComparer.Ordinal)
            .Select(g => new PreferredLanguageCount(g.Key, g.Count()))
            .ToList();
}

internal sealed record PreferredLanguageCount(string Language, int Count);
