namespace Humans.Surveys.Domain;

internal sealed record RankedAnswer(
    IReadOnlyList<IReadOnlyList<string>> RankGroups,
    IReadOnlyList<string> Rejected);
