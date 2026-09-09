namespace Humans.Surveys.Domain;

internal enum RankedVotingMethod
{
    RankedPairs,
}

internal sealed record RankedQuestionSettings(
    bool AllowEqualRanks,
    bool AllowReject,
    RankedVotingMethod OfficialMethod)
{
    public static RankedQuestionSettings Default { get; } =
        new(AllowEqualRanks: true, AllowReject: false, RankedVotingMethod.RankedPairs);
}
