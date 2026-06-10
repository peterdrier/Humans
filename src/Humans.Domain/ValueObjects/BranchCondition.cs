using Humans.Domain.Enums;

namespace Humans.Domain.ValueObjects;

/// <summary>A single branching predicate against one referenced question's selected option values. Plain jsonb payload — evaluation lives in SurveyBranchingEvaluator.</summary>
public sealed class BranchClause
{
    public Guid QuestionId { get; set; }
    public BranchOperator Operator { get; set; }
    public List<string> OptionValues { get; set; } = [];   // stable Option.Value strings
}

/// <summary>Skip-logic condition: a set of clauses combined with <see cref="BranchCombine"/>. Plain jsonb payload (mirrors CampLink) — no behaviour.</summary>
public sealed class BranchCondition
{
    public BranchCombine Combine { get; set; } = BranchCombine.All;
    public List<BranchClause> Clauses { get; set; } = [];
}
