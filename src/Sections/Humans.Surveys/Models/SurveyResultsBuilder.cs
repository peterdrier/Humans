using Humans.Surveys.Contracts;
using System.Globalization;
using Humans.Base.Extensions;
using Humans.Surveys.Services;
using Humans.Surveys.Domain;
using NodaTime;

namespace Humans.Surveys.Models;

/// <summary>
/// Assembles the dumb <see cref="SurveyResultsViewModel"/> from the service's <see cref="SurveyResultsView"/>.
/// Formatting only (percent rounding, date display, rating-average rounding) — no business logic.
/// </summary>
internal static class SurveyResultsBuilder
{
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    public static SurveyResultsViewModel Build(
        SurveyResultsView view,
        SurveyResponseExport export,
        SurveyResultsScope scope)
    {
        var rows = export.Rows.Where(row => MatchesScope(row.Anonymity, scope)).ToList();
        var questions = scope == SurveyResultsScope.Combined
            ? view.Questions
            : view.Questions.Select(question => FilterQuestion(question, rows)).ToList();

        return new SurveyResultsViewModel
        {
            SurveyId = view.SurveyId,
            Title = view.Title,
            Status = view.Status,
            InvitedCount = view.InvitedCount,
            ResponseCount = view.ResponseCount,
            ResponseRatePercent = (int)Math.Round(view.ResponseRate * 100d),
            Funnel = view.Funnel,
            Scope = scope,
            SelectedResponseCount = rows.Count,
            Questions = questions.Select(BuildQuestion).ToList(),
            Respondents = view.IdentifiedRespondents.Select(BuildRespondent).ToList(),
        };
    }

    private static bool MatchesScope(ResponseAnonymity anonymity, SurveyResultsScope scope) => scope switch
    {
        SurveyResultsScope.Unique => anonymity is ResponseAnonymity.Identified or ResponseAnonymity.CompletionTracked,
        SurveyResultsScope.Anonymous => anonymity == ResponseAnonymity.Anonymous,
        _ => true,
    };

    private static QuestionAggregate FilterQuestion(
        QuestionAggregate question,
        IReadOnlyList<SurveyExportRow> rows)
    {
        var answers = rows
            .SelectMany(row => row.Answers)
            .Where(answer => answer.QuestionId == question.QuestionId)
            .ToList();

        if (question.Type is SurveyQuestionType.SingleChoice or SurveyQuestionType.MultiChoice)
        {
            var answeredCount = answers.Count(answer => answer.SelectedValues.Count > 0);
            var options = question.OptionCounts.Select(option =>
            {
                var count = answers.Count(answer =>
                    answer.SelectedValues.Contains(option.Value, StringComparer.Ordinal));
                var percent = answeredCount == 0 ? 0d : (double)count / answeredCount * 100d;
                return option with { Count = count, Percent = percent };
            }).ToList();
            return question with { OptionCounts = options };
        }

        if (question.Type == SurveyQuestionType.Rating)
        {
            var values = answers.Where(answer => answer.RatingValue.HasValue)
                .Select(answer => answer.RatingValue!.Value)
                .ToList();
            var distribution = question.RatingDistribution
                .Select(bucket => bucket with { Count = values.Count(value => value == bucket.Value) })
                .ToList();
            return question with
            {
                RatingDistribution = distribution,
                RatingAverage = values.Count > 0 ? values.Average() : null,
            };
        }

        if (question.Type == SurveyQuestionType.Grid && question.Grid is { } grid)
        {
            var filteredRows = grid.Rows.Select(row =>
            {
                var answeredCount = answers.Count(answer =>
                    answer.GridSelections?.TryGetValue(row.Value, out var selected) == true
                    && selected.Count > 0);
                var cells = row.Cells.Select(cell =>
                {
                    var count = answers.Count(answer =>
                        answer.GridSelections?.TryGetValue(row.Value, out var selected) == true
                        && selected.Contains(cell.ColumnValue, StringComparer.Ordinal));
                    var percent = answeredCount == 0 ? 0d : (double)count / answeredCount * 100d;
                    return cell with { Count = count, Percent = percent };
                }).ToList();
                return row with { Cells = cells };
            }).ToList();
            return question with { Grid = grid with { Rows = filteredRows } };
        }

        return question with
        {
            FreeTextAnswers = answers
                .Where(answer => !string.IsNullOrEmpty(answer.TextValue))
                .Select(answer => answer.TextValue!)
                .ToList(),
        };
    }

    private static SurveyResultsQuestionViewModel BuildQuestion(QuestionAggregate q) => new()
    {
        QuestionId = q.QuestionId,
        Prompt = q.Prompt,
        Type = q.Type,
        OptionCounts = q.OptionCounts,
        Grid = q.Grid,
        RatingDistribution = q.RatingDistribution,
        RatingAverage = q.RatingAverage is { } avg ? avg.ToString("0.0", CultureInfo.InvariantCulture) : null,
        FreeTextAnswers = q.FreeTextAnswers,
    };

    private static SurveyResultsRespondentViewModel BuildRespondent(RespondentDetail r) => new()
    {
        UserId = r.UserId,
        Name = r.Name,
        SubmittedAt = FormatInstant(r.SubmittedAt),
        Answers = r.Answers,
    };

    private static string FormatInstant(Instant? instant) =>
        instant is null ? "—" : instant.Value.ToDateTime(Zone);
}

/// <summary>Admin results page: response-rate header, funnel, per-question aggregates, and the Identified drill-down.</summary>
internal sealed class SurveyResultsViewModel
{
    public Guid SurveyId { get; init; }
    public string Title { get; init; } = string.Empty;
    public SurveyStatus Status { get; init; }
    public int InvitedCount { get; init; }
    public int ResponseCount { get; init; }
    public int ResponseRatePercent { get; init; }
    public SurveyFunnel Funnel { get; init; } = new(0, 0, 0, 0);
    public SurveyResultsScope Scope { get; init; }
    public int SelectedResponseCount { get; init; }
    public IReadOnlyList<SurveyResultsQuestionViewModel> Questions { get; init; } = [];
    public IReadOnlyList<SurveyResultsRespondentViewModel> Respondents { get; init; } = [];
}

internal enum SurveyResultsScope
{
    Combined,
    Unique,
    Anonymous,
}

/// <summary>One question's display aggregate. Populated collection depends on <see cref="Type"/> (reused from the service DTO).</summary>
internal sealed class SurveyResultsQuestionViewModel
{
    public Guid QuestionId { get; init; }
    public string Prompt { get; init; } = string.Empty;
    public SurveyQuestionType Type { get; init; }
    public IReadOnlyList<OptionCount> OptionCounts { get; init; } = [];
    public GridAggregate? Grid { get; init; }
    public IReadOnlyList<RatingBucket> RatingDistribution { get; init; } = [];
    public string? RatingAverage { get; init; }
    public IReadOnlyList<string> FreeTextAnswers { get; init; } = [];

    public int RatingMaxCount => RatingDistribution.Count == 0 ? 0 : RatingDistribution.Max(b => b.Count);
}

/// <summary>One Identified respondent's drill-down row (name + formatted submit time + their answers).</summary>
internal sealed class SurveyResultsRespondentViewModel
{
    public Guid UserId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SubmittedAt { get; init; } = "—";
    public IReadOnlyList<RespondentAnswer> Answers { get; init; } = [];
}
