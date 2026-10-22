namespace Humans.Surveys.Contracts;

/// <summary>
/// Prompts and labels resolved in <see cref="DefaultCulture"/>; questions in page-then-order sequence.
/// </summary>
public sealed record SurveyDefinitionSnapshot(
    Guid Id,
    string Title,
    SurveyStatus Status,
    string DefaultCulture,
    IReadOnlyList<SurveyDefinitionQuestion> Questions);

/// <summary>
/// The populated fields depend on <see cref="Type"/>: <c>Markdown</c> for Information, the rating bounds for Rating,
/// the grid shape for Grid, <see cref="Options"/> for the choice types.
/// </summary>
public sealed record SurveyDefinitionQuestion(
    Guid Id,
    int Page,
    int Order,
    SurveyQuestionType Type,
    string Prompt,
    string? Markdown,
    bool Required,
    int? RatingMin,
    int? RatingMax,
    SurveyBranchCondition? ShowIf,
    GridSelectionMode? GridSelectionMode,
    IReadOnlyList<SurveyExportGridRow> GridRows,
    IReadOnlyList<SurveyDefinitionImage> Images,
    IReadOnlyList<SurveyExportOption> Options,
    SurveyRankedSettings? RankedSettings = null,
    IReadOnlyList<string>? RankedUnavailableOptionValues = null);

/// <summary>An app-relative URL plus resolved captions.</summary>
public sealed record SurveyDefinitionImage(Guid Id, string Url, string Label, string AltText);

/// <summary>
/// <c>All</c> = AND, <c>Any</c> = OR. A public mirror of the stored jsonb, so the persisted shape stays free to change.
/// </summary>
public sealed record SurveyBranchCondition(string Combine, IReadOnlyList<SurveyBranchClause> Clauses);

/// <summary>
/// <see cref="Operator"/> is <c>Is</c>, <c>IsNot</c>, <c>Answered</c> or <c>NotAnswered</c>, applied to the
/// referenced question's selected option values.
/// </summary>
public sealed record SurveyBranchClause(Guid QuestionId, string Operator, IReadOnlyList<string> OptionValues);
