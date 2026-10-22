namespace Humans.Governance.Domain;

internal enum VoteChoice
{
    Yay = 0,

    /// <summary>Leaning yes but has concerns.</summary>
    Maybe = 1,

    No = 2,

    Abstain = 3
}
