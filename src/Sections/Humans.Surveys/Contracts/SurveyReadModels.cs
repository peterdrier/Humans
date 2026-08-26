using NodaTime;

namespace Humans.Surveys.Contracts;

/// <summary>A survey and its participation counts — the machine API's list row and the admin index.</summary>
public sealed record SurveySummary(Guid Id, string Title, SurveyStatus Status, int InvitedCount, int ResponseCount);

// ── Results DTOs (co-located) ───────────────────────────────────────────────

/// <summary>
/// The admin results read model. <see cref="ResponseRate"/> is completed sent invitations ÷
/// <see cref="InvitedCount"/> (0 when no one was invited); Anonymous responses and unsent public
/// participation do not count. All prompts/labels are resolved in the survey's default culture.
/// </summary>
public sealed record SurveyResultsView(
    Guid SurveyId,
    string Title,
    SurveyStatus Status,
    int InvitedCount,
    int ResponseCount,
    double ResponseRate,
    SurveyFunnel Funnel,
    IReadOnlyList<QuestionAggregate> Questions,
    IReadOnlyList<RespondentDetail> IdentifiedRespondents);

/// <summary>
/// Participation funnel split by entry path: the user-specific link path (per-invitation
/// <c>Started</c> flag vs submitted link responses) and the public slug path (the survey's
/// <c>PublicStartedCount</c> vs submitted slug responses).
/// </summary>
public sealed record SurveyFunnel(int LinkStarted, int LinkFinished, int SlugStarted, int SlugFinished);

/// <summary>
/// One question's aggregate over submitted responses. The populated collection depends on the
/// question type: <see cref="OptionCounts"/> for choice questions, <see cref="RatingDistribution"/>
/// plus <see cref="RatingAverage"/> for rating questions, <see cref="FreeTextAnswers"/> for text
/// questions, and <see cref="Grid"/> for Grid questions. The others are empty/null.
/// </summary>
public sealed record QuestionAggregate(
    Guid QuestionId,
    string Prompt,
    SurveyQuestionType Type,
    IReadOnlyList<OptionCount> OptionCounts,
    IReadOnlyList<RatingBucket> RatingDistribution,
    double? RatingAverage,
    IReadOnlyList<string> FreeTextAnswers,
    GridAggregate? Grid = null);

/// <summary>One choice option's tally. <see cref="Percent"/> is the share of responses to that question (0 when none).</summary>
public sealed record OptionCount(string Value, string Label, int Count, double Percent);

/// <summary>One rating value's tally; empty buckets are included across the question's range.</summary>
public sealed record RatingBucket(int Value, int Count);

/// <summary>A Grid question's resolved column schema and per-row cell counts.</summary>
public sealed record GridAggregate(
    GridSelectionMode Mode,
    IReadOnlyList<SurveyExportOption> Columns,
    IReadOnlyList<GridAggregateRow> Rows);

/// <summary>One resolved Grid row and its cells.</summary>
public sealed record GridAggregateRow(string Value, string Label, IReadOnlyList<GridCellCount> Cells);

/// <summary>One Grid cell tally. Percent is based on respondents who answered the row.</summary>
public sealed record GridCellCount(string ColumnValue, string ColumnLabel, int Count, double Percent);

/// <summary>One Identified respondent's drill-down row: stitched display name + their answers.</summary>
public sealed record RespondentDetail(Guid UserId, string Name, Instant? SubmittedAt, IReadOnlyList<RespondentAnswer> Answers);

/// <summary>One answer in an Identified respondent's drill-down, with choice labels resolved in the default culture.</summary>
public sealed record RespondentAnswer(
    Guid QuestionId,
    string Prompt,
    IReadOnlyList<string> SelectedLabels,
    string? TextValue,
    int? RatingValue,
    IReadOnlyList<ResolvedGridSelection>? GridSelections = null);

/// <summary>One resolved Grid row selection for display/export.</summary>
public sealed record ResolvedGridSelection(
    string RowValue,
    string RowLabel,
    IReadOnlyList<string> ColumnValues,
    IReadOnlyList<string> ColumnLabels);

// ── Export DTOs (co-located; raw per-response, shared by CSV/JSON download and the analysis API) ──

/// <summary>
/// The raw export of a survey's submitted responses: the question schema (ordered by page then order)
/// plus one row per response (ordered by submission time). Prompts/labels are resolved in
/// <see cref="DefaultCulture"/>.
/// </summary>
public sealed record SurveyResponseExport(
    Guid SurveyId,
    string Title,
    string DefaultCulture,
    IReadOnlyList<SurveyExportQuestion> Questions,
    IReadOnlyList<SurveyExportRow> Rows);

/// <summary>One question in the export schema. <see cref="Options"/> is empty for non-choice questions.</summary>
public sealed record SurveyExportQuestion(
    Guid QuestionId,
    string Prompt,
    SurveyQuestionType Type,
    IReadOnlyList<SurveyExportOption> Options,
    GridSelectionMode? GridSelectionMode = null,
    IReadOnlyList<SurveyExportGridRow>? GridRows = null);

/// <summary>One choice option in the export schema: the stable machine <see cref="Value"/> + its resolved <see cref="Label"/>.</summary>
public sealed record SurveyExportOption(string Value, string Label);

/// <summary>One Grid row in the export schema.</summary>
public sealed record SurveyExportGridRow(string Value, string Label);

/// <summary>
/// One exported response. <see cref="UserId"/>/<see cref="UserName"/> are populated only for
/// <see cref="ResponseAnonymity.Identified"/> rows; both are null for CompletionTracked/Anonymous.
/// </summary>
public sealed record SurveyExportRow(
    Guid ResponseId,
    ResponseAnonymity Anonymity,
    SurveyInputMethod InputMethod,
    string Culture,
    Instant? SubmittedAt,
    Guid? UserId,
    string? UserName,
    IReadOnlyList<SurveyExportAnswer> Answers);

/// <summary>
/// One answer in an exported response: raw stable keys plus best-effort resolved labels,
/// free text, or a rating.
/// </summary>
public sealed record SurveyExportAnswer(
    Guid QuestionId,
    IReadOnlyList<string> SelectedValues,
    IReadOnlyList<string> SelectedLabels,
    string? TextValue,
    int? RatingValue,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GridSelections = null,
    IReadOnlyList<ResolvedGridSelection>? GridSelectionLabels = null);
