namespace Humans.Domain.Enums;

/// <summary>Predicate operator for a single branching clause against a referenced question's answer.</summary>
public enum BranchOperator
{
    Is = 0,
    IsNot = 1,
    Answered = 2,
    NotAnswered = 3
}
