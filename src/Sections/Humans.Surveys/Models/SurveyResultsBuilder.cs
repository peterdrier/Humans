using Humans.Surveys.Contracts;
using System.Globalization;
using Humans.Base.Extensions;
using Humans.Surveys.Services;
using NodaTime;

namespace Humans.Surveys.Models;

/// <summary>Formatting only (percent, date and rating-average rounding): no business logic.</summary>
internal static class SurveyResultsBuilder
{
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    public static SurveyResultsViewModel Build(SurveyScopedResults scoped)
    {
        var view = scoped.Results;

        return new SurveyResultsViewModel
        {
            SurveyId = view.SurveyId,
            Title = view.Title,
            Status = view.Status,
            InvitedCount = view.InvitedCount,
            ResponseCount = view.ResponseCount,
            ResponseRatePercent = (int)Math.Round(view.ResponseRate * 100d),
            Funnel = view.Funnel,
            Scope = scoped.Scope,
            SelectedResponseCount = scoped.SelectedResponseCount,
            Questions = view.Questions.OrderBy(q => q.PageNumber).ThenBy(q => q.Order).Select(BuildQuestion).ToList(),
            Respondents = view.IdentifiedRespondents
                .OrderBy(respondent => respondent.SubmittedAt)
                .ThenBy(respondent => respondent.ResponseId)
                .Select(BuildRespondent).ToList(),
            IsEmbargoed = scoped.IsEmbargoed,
            RankedQuestions = scoped.RankedQuestions ?? new Dictionary<Guid, RankedQuestionResult>(),
            IsAsociadoVote = scoped.IsAsociadoVote,
            UnattributedBallots = (scoped.UnattributedBallots ?? [])
                .Select(ballot => new SurveyResultsBallotViewModel { Answers = ballot.Answers })
                .ToList(),
        };
    }

    private static SurveyResultsQuestionViewModel BuildQuestion(QuestionAggregate q) => new()
    {
        QuestionId = q.QuestionId,
        Prompt = q.Prompt,
        Type = q.Type,
        OptionCounts = q.OptionCounts.OrderBy(option => option.Order).ToList(),
        Grid = q.Grid,
        RatingDistribution = q.RatingDistribution,
        RatingAverage = q.RatingAverage is { } avg ? avg.ToString("0.0", CultureInfo.CurrentUICulture) : null,
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
    public bool ShowIdentifiedRespondents => !IsAsociadoVote && Scope != SurveyResultsScope.Anonymous;
    public bool ShowUnattributedBallots => IsAsociadoVote && !IsEmbargoed;
    public bool IsEmbargoed { get; init; }
    public bool IsAsociadoVote { get; init; }
    public IReadOnlyList<SurveyResultsBallotViewModel> UnattributedBallots { get; init; } = [];
    public IReadOnlyDictionary<Guid, RankedQuestionResult> RankedQuestions { get; init; }
        = new Dictionary<Guid, RankedQuestionResult>();
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

internal sealed class SurveyResultsRespondentViewModel
{
    public Guid UserId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string SubmittedAt { get; init; } = "—";
    public IReadOnlyList<RespondentAnswer> Answers { get; init; } = [];
}

/// <summary>Deliberately without identity or timestamp.</summary>
internal sealed class SurveyResultsBallotViewModel
{
    public IReadOnlyList<RespondentAnswer> Answers { get; init; } = [];
}
