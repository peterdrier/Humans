namespace Humans.Surveys.Domain;

/// <summary>Predicate operator for a single branching clause against a referenced question's answer.</summary>
internal enum BranchOperator
{
    Is = 0,
    IsNot = 1,
    Answered = 2,
    NotAnswered = 3
}
