using Humans.Surveys.Contracts;
using System.Globalization;
using Humans.Base.Extensions;
using Humans.Surveys.Services;
using NodaTime;

namespace Humans.Surveys.Models;

/// <summary>Formatting only (percent, date and rating-average rounding): no business logic.</summary>
internal static class SurveyResultsBuilder
{
    public static SurveyResultsViewModel Build(SurveyScopedResults scoped, DateTimeZone zone)
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
            Questions = BuildQuestions(view.Questions),
            Respondents = view.IdentifiedRespondents
                .OrderBy(respondent => respondent.SubmittedAt)
                .ThenBy(respondent => respondent.ResponseId)
                .Select(r => BuildRespondent(r, zone)).ToList(),
            IsEmbargoed = scoped.IsEmbargoed,
            RankedQuestions = scoped.RankedQuestions ?? new Dictionary<Guid, RankedQuestionResult>(),
            IsAsociadoVote = scoped.IsAsociadoVote,
            UnattributedBallots = (scoped.UnattributedBallots ?? [])
                .Select(ballot => new SurveyResultsBallotViewModel { Answers = ballot.Answers })
                .ToList(),
        };
    }

    /// <summary>Numbers questions in survey order and moves each follow-up inside the question it follows.</summary>
    private static IReadOnlyList<SurveyResultsQuestionViewModel> BuildQuestions(IReadOnlyList<QuestionAggregate> aggregates)
    {
        var all = aggregates
            .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
            .Select((q, index) => BuildQuestion(q, index + 1))
            .ToList();
        var byId = all.ToDictionary(q => q.QuestionId);
        var top = new List<SurveyResultsQuestionViewModel>();
        foreach (var q in all)
        {
            if (q.FollowUpOf is { } parentId && byId.TryGetValue(parentId, out var parent) && parent.FollowUpOf is null)
                parent.FollowUps.Add(q);
            else
                top.Add(q);
        }
        return top;
    }

    private static SurveyResultsQuestionViewModel BuildQuestion(QuestionAggregate q, int number) => new()
    {
        Number = number,
        HelpText = q.HelpText,
        FollowUpOf = q.FollowUpOf,
        AskedWhen = q.AskedWhen,
        FreeTextByParentChoice = q.FreeTextByParentChoice,
        QuestionId = q.QuestionId,
        Prompt = q.Prompt,
        Type = q.Type,
        OptionCounts = q.OptionCounts.OrderBy(option => option.Order).ToList(),
        Grid = q.Grid,
        RatingDistribution = q.RatingDistribution,
        RatingAverage = q.RatingAverage is { } avg ? avg.ToString("0.0", CultureInfo.CurrentUICulture) : null,
        FreeTextAnswers = q.FreeTextAnswers,
    };

    private static SurveyResultsRespondentViewModel BuildRespondent(RespondentDetail r, DateTimeZone zone) => new()
    {
        UserId = r.UserId,
        SubmittedAt = FormatInstant(r.SubmittedAt, zone),
        Answers = r.Answers,
    };

    private static string FormatInstant(Instant? instant, DateTimeZone zone) =>
        instant is null ? "—" : instant.Value.ToDateTime(zone);
}

internal sealed class SurveyResultsViewModel
{
    public Guid SurveyId { get; init; }

    /// <summary>The downloads: Surveys Admin and Admin, or the author once closed. Board reads the page only.</summary>
    public bool CanExport { get; set; }
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

    /// <summary>Every question, follow-ups included, by id: numbers and nesting for the per-respondent answers.</summary>
    public IReadOnlyDictionary<Guid, SurveyResultsQuestionViewModel> QuestionsById =>
        Questions.Concat(Questions.SelectMany(q => q.FollowUps)).ToDictionary(q => q.QuestionId);
}

/// <summary>One respondent's or ballot's answers, with the page's question numbering and follow-up nesting.</summary>
internal sealed record SurveyRespondentAnswersViewModel(
    IReadOnlyList<RespondentAnswer> Answers,
    IReadOnlyDictionary<Guid, SurveyResultsQuestionViewModel> Questions);

/// <summary>One question's display aggregate. Populated collection depends on <see cref="Type"/> (reused from the service DTO).</summary>
internal sealed class SurveyResultsQuestionViewModel
{
    public int Number { get; init; }
    public string HelpText { get; init; } = string.Empty;
    public Guid? FollowUpOf { get; init; }
    public IReadOnlyList<string> AskedWhen { get; init; } = [];
    public IReadOnlyList<FreeTextGroup> FreeTextByParentChoice { get; init; } = [];
    public List<SurveyResultsQuestionViewModel> FollowUps { get; } = [];
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
    public string SubmittedAt { get; init; } = "—";
    public IReadOnlyList<RespondentAnswer> Answers { get; init; } = [];
}

/// <summary>Deliberately without identity or timestamp.</summary>
internal sealed class SurveyResultsBallotViewModel
{
    public IReadOnlyList<RespondentAnswer> Answers { get; init; } = [];
}
