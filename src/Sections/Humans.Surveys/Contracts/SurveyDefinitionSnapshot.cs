namespace Humans.Surveys.Contracts;

/// <summary>
/// A survey's question graph as a machine reader sees it: prompts and labels already
/// resolved in <see cref="DefaultCulture"/>, questions in page-then-order sequence, and every
/// option carrying the stable <c>Value</c> the response export joins on.
/// </summary>
public sealed record SurveyDefinitionSnapshot(
    Guid Id,
    string Title,
    SurveyStatus Status,
    string DefaultCulture,
    IReadOnlyList<SurveyDefinitionQuestion> Questions);

/// <summary>
/// One question. Which fields are populated depends on <see cref="Type"/>: <c>Markdown</c> for
/// Information, the rating bounds for Rating, the grid shape for Grid, <see cref="Options"/>
/// for the choice types.
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
    IReadOnlyList<SurveyExportOption> Options);

/// <summary>An Information question's image: an app-relative URL plus its resolved captions.</summary>
public sealed record SurveyDefinitionImage(Guid Id, string Url, string Label, string AltText);

/// <summary>
/// Skip logic on a question: show it when <see cref="Clauses"/> hold, combined per
/// <see cref="Combine"/> (<c>All</c> = AND, <c>Any</c> = OR). A public mirror of the stored
/// jsonb payload, so the persisted shape stays free to change.
/// </summary>
public sealed record SurveyBranchCondition(string Combine, IReadOnlyList<SurveyBranchClause> Clauses);

/// <summary>
/// One predicate: <see cref="Operator"/> (<c>Is</c>, <c>IsNot</c>, <c>Answered</c>,
/// <c>NotAnswered</c>) applied to the referenced question's selected option values.
/// </summary>
public sealed record SurveyBranchClause(Guid QuestionId, string Operator, IReadOnlyList<string> OptionValues);
