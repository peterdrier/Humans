using NodaTime;

namespace Humans.Surveys.Contracts;

/// <summary>
/// <c>CreatedByUserId</c> is <see cref="Guid.Empty"/> for an unattributed survey or one whose author was erased.
/// </summary>
public sealed record SurveySummary(
    Guid Id, string Title, SurveyStatus Status, int InvitedCount, int ResponseCount, Guid CreatedByUserId);

/// <summary>
/// <see cref="ResponseRate"/> is completed sent invitations ÷ <see cref="InvitedCount"/> (0 when no one was invited);
/// Anonymous responses and unsent public participation do not count. Labels are in the default culture.
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
/// Link path: per-invitation <c>Started</c> flags vs submitted link responses. Slug path: the survey's
/// <c>PublicStartedCount</c> vs submitted slug responses.
/// </summary>
public sealed record SurveyFunnel(int LinkStarted, int LinkFinished, int SlugStarted, int SlugFinished);

/// <summary>
/// Over submitted responses. Populated by type: <see cref="OptionCounts"/> for choice, <see cref="RatingDistribution"/>
/// and <see cref="RatingAverage"/> for rating, <see cref="FreeTextAnswers"/> for text, <see cref="Grid"/> for Grid.
/// The others are empty or null.
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

/// <summary><see cref="Percent"/> is the share of responses to that question (0 when none).</summary>
public sealed record OptionCount(string Value, string Label, int Count, double Percent);

/// <summary>Empty buckets are included across the question's range.</summary>
public sealed record RatingBucket(int Value, int Count);

public sealed record GridAggregate(
    GridSelectionMode Mode,
    IReadOnlyList<SurveyExportOption> Columns,
    IReadOnlyList<GridAggregateRow> Rows);

public sealed record GridAggregateRow(string Value, string Label, IReadOnlyList<GridCellCount> Cells);

/// <summary>Percent is based on respondents who answered the row.</summary>
public sealed record GridCellCount(string ColumnValue, string ColumnLabel, int Count, double Percent);

/// <summary>Identified respondents only.</summary>
public sealed record RespondentDetail(Guid UserId, string Name, Instant? SubmittedAt, IReadOnlyList<RespondentAnswer> Answers)
{
    public Guid ResponseId { get; init; }
}

/// <summary>Choice labels resolved in the default culture.</summary>
public sealed record RespondentAnswer(
    Guid QuestionId,
    string Prompt,
    IReadOnlyList<string> SelectedLabels,
    string? TextValue,
    int? RatingValue,
    IReadOnlyList<ResolvedGridSelection>? GridSelections = null,
    ResolvedRankedBallot? RankedBallot = null);

public sealed record ResolvedGridSelection(
    string RowValue,
    string RowLabel,
    IReadOnlyList<string> ColumnValues,
    IReadOnlyList<string> ColumnLabels);

public sealed record ResolvedRankedBallot(
    IReadOnlyList<IReadOnlyList<string>> RankGroups,
    IReadOnlyList<string> Rejected);

/// <summary>
/// Questions ordered by page then order; one row per response, by submission time. Labels resolved in
/// <see cref="DefaultCulture"/>.
/// </summary>
public sealed record SurveyResponseExport(
    Guid SurveyId,
    string Title,
    string DefaultCulture,
    IReadOnlyList<SurveyExportQuestion> Questions,
    IReadOnlyList<SurveyExportRow> Rows);

/// <summary><see cref="Options"/> is empty for non-choice questions.</summary>
public sealed record SurveyExportQuestion(
    Guid QuestionId,
    string Prompt,
    SurveyQuestionType Type,
    IReadOnlyList<SurveyExportOption> Options,
    GridSelectionMode? GridSelectionMode = null,
    IReadOnlyList<SurveyExportGridRow>? GridRows = null,
    SurveyRankedSettings? RankedSettings = null,
    IReadOnlyList<string>? RankedUnavailableOptionValues = null);

/// <summary><see cref="Value"/> is the stable machine key.</summary>
public sealed record SurveyExportOption(string Value, string Label);

public sealed record SurveyExportGridRow(string Value, string Label);

public sealed record SurveyRankedSettings(
    bool AllowEqualRanks,
    bool AllowReject,
    string OfficialMethod);

/// <summary>Uses stable option values.</summary>
public sealed record SurveyRankedBallot(
    IReadOnlyList<IReadOnlyList<string>> RankGroups,
    IReadOnlyList<string> Rejected);

/// <summary>
/// One exported response. <see cref="UserId"/>/<see cref="UserName"/> are populated only for
/// <see cref="ResponseAnonymity.Identified"/> rows; both are null for CompletionTracked/Anonymous.
/// Asociado-vote rows suppress identity and <see cref="SubmittedAt"/> regardless of anonymity.
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

/// <summary>Raw stable keys plus best-effort resolved labels, free text, or a rating.</summary>
public sealed record SurveyExportAnswer(
    Guid QuestionId,
    IReadOnlyList<string> SelectedValues,
    IReadOnlyList<string> SelectedLabels,
    string? TextValue,
    int? RatingValue,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? GridSelections = null,
    IReadOnlyList<ResolvedGridSelection>? GridSelectionLabels = null,
    SurveyRankedBallot? RankedBallot = null);
