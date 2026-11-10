using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Humans.Users.Contracts;

namespace Humans.Surveys.Services;

internal sealed class SurveyDataExport(ISurveyRepository repo, IUserService userService) : IApplicationService
{
    internal const string SurveyResponses = "SurveyResponses";
    internal const string AuthoredSurveys = "AuthoredSurveys";
    internal const string SurveyInvitations = "SurveyInvitations";

    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await userService.GetUserInfoAsync(userId, ct);
        var userIds = user?.AllUserIds ?? [userId];
        var responses = new List<SurveyResponse>();
        var invitations = new List<SurveyInvitation>();
        foreach (var id in userIds)
        {
            responses.AddRange(await repo.GetIdentifiedResponsesForUserAsync(id, ct));
            invitations.AddRange(await repo.GetInvitationsForUserAsync(id, ct));
        }

        // Load each distinct survey definition once (few at this scale) to resolve titles, prompts, and labels.
        var definitions = new Dictionary<Guid, Survey>();
        foreach (var surveyId in responses.Select(r => r.SurveyId).Distinct())
        {
            var survey = await repo.GetByIdAsync(surveyId, ct);
            if (survey is not null) definitions[surveyId] = survey;
        }

        var shaped = responses
            .OrderBy(r => r.SubmittedAt)
            .Select(r =>
            {
                definitions.TryGetValue(r.SurveyId, out var survey);
                var culture = string.IsNullOrEmpty(r.Culture)
                    ? survey?.DefaultCulture ?? "en"
                    : r.Culture;

                var answerContext = new SurveyAnswerProjection(survey, culture);

                return new
                {
                    Survey = survey?.Title.Resolve(culture, survey.DefaultCulture) ?? r.SurveyId.ToString(),
                    SubmittedAt = r.SubmittedAt.ToIso8601(),
                    Culture = culture,
                    Answers = r.Answers.Select(a => new
                    {
                        Question = answerContext.PromptFor(a),
                        SelectedLabels = answerContext.ResolveSelectedLabels(a),
                        GridSelections = SurveyAnswerProjection.CopyGridSelections(a),
                        GridSelectionLabels = answerContext.ResolveGridSelections(a),
                        RankedBallot = SurveyAnswerProjection.CopyRankedBallot(a),
                        RankedBallotLabels = answerContext.ResolveRankedBallot(a),
                        a.TextValue,
                        a.RatingValue,
                    }).ToList(),
                };
            })
            .ToList();

        // Authoring is open to any approved human, so a member's own surveys are their
        // personal data too — Drafts nobody else can see included.
        var authored = (await repo.GetSurveysAuthoredByAsync(user?.Id ?? userId, ct))
            .OrderBy(s => s.CreatedAt)
            .Select(s => new
            {
                Survey = s.Title.Resolve(s.DefaultCulture, s.DefaultCulture),
                Status = s.Status.ToString(),
                s.RejectionNote,
                CreatedAt = s.CreatedAt.ToIso8601(),
                QuestionCount = s.Questions.Count
            })
            .ToList();

        // The invitation ledger. Most responses are not Identified, so for someone who was only
        // invited — or who answered under completion tracking — this is the only record of theirs
        // the section holds. The timestamps here are invitation-side and say nothing about when a
        // response arrived, so they do not correlate with an anonymous answer.
        foreach (var surveyId in invitations.Select(i => i.SurveyId).Distinct())
        {
            if (definitions.ContainsKey(surveyId)) continue;
            var survey = await repo.GetByIdAsync(surveyId, ct);
            if (survey is not null) definitions[surveyId] = survey;
        }

        var invited = invitations
            .OrderBy(i => i.CreatedAt)
            .Select(i =>
            {
                definitions.TryGetValue(i.SurveyId, out var survey);
                var culture = survey?.DefaultCulture ?? "en";
                return new
                {
                    Survey = survey?.Title.Resolve(culture, culture) ?? i.SurveyId.ToString(),
                    InvitedAt = i.CreatedAt.ToIso8601(),
                    SentAt = i.SentAt.ToIso8601(),
                    EmailStatus = i.LatestEmailStatus?.ToString(),
                    ReminderSentAt = i.ReminderSentAt.ToIso8601(),
                    i.Started,
                    i.Completed
                };
            })
            .ToList();

        return
        [
            new UserDataSlice(SurveyResponses, shaped),
            new UserDataSlice(AuthoredSurveys, authored),
            new UserDataSlice(SurveyInvitations, invited)
        ];
    }
}
