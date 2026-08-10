namespace Humans.Surveys.Domain;

/// <summary>How a <c>BranchCondition</c>'s clauses combine: <see cref="All"/> = AND, <see cref="Any"/> = OR.</summary>
internal enum BranchCombine
{
    All = 0,
    Any = 1
}
