using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Surveys;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Surveys;

/// <summary>
/// Application-layer <see cref="ISurveyService"/>. Plain Scoped service (no caching decorator, per
/// spec §12). Cross-domain display data is stitched from <c>I…ServiceRead</c> interfaces — the
/// repository never resolves user/team navs. Authoring lives here; send/submit/results/export/GDPR
/// are added in their phases (constructor grows with them).
/// </summary>
public sealed class SurveyService(
    ISurveyRepository repo,
    IAuditLogService auditLog,
    IClock clock,
    ILogger<SurveyService> logger) : ISurveyService
{
    public async Task<IReadOnlyList<SurveySummary>> GetSummariesAsync(CancellationToken ct = default)
    {
        var surveys = await repo.GetAllSummariesAsync(ct);
        var invited = await repo.GetInvitedCountsBySurveyAsync(ct);
        var responses = await repo.GetResponseCountsBySurveyAsync(ct);

        return surveys.Select(s => new SurveySummary(
            s.Id,
            s.Title.Resolve(s.DefaultCulture, s.DefaultCulture),
            s.Status,
            invited.GetValueOrDefault(s.Id),
            responses.GetValueOrDefault(s.Id))).ToList();
    }

    public async Task<SurveyDetail?> GetForEditAsync(Guid surveyId, CancellationToken ct = default)
    {
        var s = await repo.GetByIdAsync(surveyId, ct);
        if (s is null) return null;

        var input = new SurveyEditInput(
            s.Title, s.Intro, s.ThankYou, s.DefaultCulture, s.AllowAnonymous, s.OpensAt, s.ClosesAt,
            s.AudienceType, s.AudienceTeamId, s.PublicSlug,
            s.Questions
                .OrderBy(q => q.PageNumber).ThenBy(q => q.Order)
                .Select(q => new QuestionInput(
                    q.Id, q.PageNumber, q.Order, q.Type, q.Prompt, q.HelpText, q.IsRequired,
                    q.RatingMin, q.RatingMax, q.RatingMinLabel, q.RatingMaxLabel, q.ShowIf,
                    q.Options.OrderBy(o => o.Order)
                        .Select(o => new OptionInput(o.Id, o.Order, o.Value, o.Label)).ToList()))
                .ToList());

        return new SurveyDetail(s.Id, s.Status, input);
    }

    public async Task<Guid> CreateAsync(SurveyEditInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var surveyId = Guid.NewGuid();
        var questions = MapQuestions(surveyId, input);
        ValidateBranching(questions);

        var survey = new Survey
        {
            Id = surveyId,
            Title = input.Title,
            Intro = input.Intro,
            ThankYou = input.ThankYou,
            DefaultCulture = input.DefaultCulture,
            AllowAnonymous = input.AllowAnonymous,
            Status = SurveyStatus.Draft,
            OpensAt = input.OpensAt,
            ClosesAt = input.ClosesAt,
            AudienceType = input.AudienceType,
            AudienceTeamId = input.AudienceTeamId,
            PublicSlug = NormalizeSlug(input.PublicSlug),
            CreatedByUserId = actorUserId,
            CreatedAt = now,
            UpdatedAt = now,
            Questions = questions,
        };

        await repo.AddAsync(survey, ct);
        logger.LogInformation("Survey {SurveyId} created by {UserId}", surveyId, actorUserId);
        await auditLog.LogAsync(AuditAction.SurveyCreated, nameof(Survey), surveyId,
            $"Created survey '{survey.Title.Resolve(survey.DefaultCulture, survey.DefaultCulture)}'", actorUserId);
        return surveyId;
    }

    public async Task UpdateAsync(Guid surveyId, SurveyEditInput input, Guid actorUserId, CancellationToken ct = default)
    {
        var now = clock.GetCurrentInstant();
        var questions = MapQuestions(surveyId, input);
        ValidateBranching(questions);

        var survey = new Survey
        {
            Id = surveyId,
            Title = input.Title,
            Intro = input.Intro,
            ThankYou = input.ThankYou,
            DefaultCulture = input.DefaultCulture,
            AllowAnonymous = input.AllowAnonymous,
            OpensAt = input.OpensAt,
            ClosesAt = input.ClosesAt,
            AudienceType = input.AudienceType,
            AudienceTeamId = input.AudienceTeamId,
            PublicSlug = NormalizeSlug(input.PublicSlug),
            UpdatedAt = now,
            Questions = questions,
        };

        await repo.UpdateAsync(survey, ct);
        await auditLog.LogAsync(AuditAction.SurveyUpdated, nameof(Survey), surveyId, "Updated survey", actorUserId);
    }

    public async Task OpenAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var status = await repo.GetStatusAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (status == SurveyStatus.Open) return;

        await repo.SetStatusAsync(surveyId, SurveyStatus.Open, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(AuditAction.SurveyOpened, nameof(Survey), surveyId, "Opened survey", actorUserId);
    }

    public async Task CloseAsync(Guid surveyId, Guid actorUserId, CancellationToken ct = default)
    {
        var status = await repo.GetStatusAsync(surveyId, ct)
            ?? throw new InvalidOperationException("Survey not found.");
        if (status == SurveyStatus.Closed) return;

        await repo.SetStatusAsync(surveyId, SurveyStatus.Closed, clock.GetCurrentInstant(), ct);
        await auditLog.LogAsync(AuditAction.SurveyClosed, nameof(Survey), surveyId, "Closed survey", actorUserId);
    }

    /// <summary>Maps builder input to tracked entities, assigning new ids where the input id is null.</summary>
    private static List<SurveyQuestion> MapQuestions(Guid surveyId, SurveyEditInput input)
        => input.Questions.Select(q =>
        {
            var questionId = q.Id ?? Guid.NewGuid();
            return new SurveyQuestion
            {
                Id = questionId,
                SurveyId = surveyId,
                PageNumber = q.PageNumber,
                Order = q.Order,
                Type = q.Type,
                Prompt = q.Prompt,
                HelpText = q.HelpText,
                IsRequired = q.IsRequired,
                RatingMin = q.RatingMin,
                RatingMax = q.RatingMax,
                RatingMinLabel = q.RatingMinLabel,
                RatingMaxLabel = q.RatingMaxLabel,
                ShowIf = q.ShowIf,
                Options = q.Options.Select(o => new SurveyQuestionOption
                {
                    Id = o.Id ?? Guid.NewGuid(),
                    QuestionId = questionId,
                    Order = o.Order,
                    Value = o.Value,
                    Label = o.Label,
                }).ToList<SurveyQuestionOption>(),
            };
        }).ToList();

    private static void ValidateBranching(IReadOnlyList<SurveyQuestion> questions)
    {
        var offenders = SurveyBranchingEvaluator.ValidateNoForwardReferences(questions);
        if (offenders.Count > 0)
        {
            throw new InvalidOperationException(
                $"A branching condition references a question that is not strictly earlier. Offending question ids: {string.Join(", ", offenders)}.");
        }
    }

    private static string? NormalizeSlug(string? slug)
        => string.IsNullOrWhiteSpace(slug) ? null : slug.Trim().ToLowerInvariant();
}
