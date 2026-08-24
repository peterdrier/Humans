using AwesomeAssertions;
using Humans.Integration.Tests.Infrastructure;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using Xunit;

namespace Humans.Integration.Tests.Repositories.Surveys;

public class SurveyRepositoryAnswerPersistenceTests(HumansTestDatabase database)
    : IntegrationTestBase(database)
{
    [HumansFact]
    public async Task IdentifiedDraft_InsertsReplacementAnswers_OnAutosaveAndFinalise()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var scope = Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SurveysDbContext>();
        var repo = scope.ServiceProvider.GetRequiredService<ISurveyRepository>();
        var now = Instant.FromUtc(2026, 8, 24, 5, 0);
        var userId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var draftId = Guid.NewGuid();

        db.Surveys.Add(new Survey
        {
            Id = surveyId,
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            Questions =
            [
                new SurveyQuestion
                {
                    Id = questionId,
                    SurveyId = surveyId,
                    PageNumber = 1,
                    Order = 1,
                    Type = SurveyQuestionType.ShortText,
                },
            ],
        });
        db.SurveyInvitations.Add(new SurveyInvitation
        {
            Id = invitationId,
            SurveyId = surveyId,
            UserId = userId,
            CreatedAt = now,
        });
        db.SurveyResponses.Add(new SurveyResponse
        {
            Id = draftId,
            SurveyId = surveyId,
            InvitationId = invitationId,
            UserId = userId,
            Anonymity = ResponseAnonymity.Identified,
            InputMethod = SurveyInputMethod.UserSpecificLink,
            Culture = "en",
        });
        await db.SaveChangesAsync(ct);

        var first = Answer(draftId, questionId, "first");
        await repo.SaveDraftAnswersAsync(
            draftId, [first], SurveyInputMethod.UserSpecificLink, "en", ct);
        (await StoredAnswers(db, draftId, ct)).Should().ContainSingle()
            .Which.Id.Should().Be(first.Id);

        var second = Answer(draftId, questionId, "second");
        await repo.SaveDraftAnswersAsync(
            draftId, [second], SurveyInputMethod.Slug, "es", ct);
        (await StoredAnswers(db, draftId, ct)).Should().ContainSingle()
            .Which.Id.Should().Be(second.Id);

        var final = Answer(draftId, questionId, "final");
        await repo.FinalizeIdentifiedResponseAsync(
            invitationId, draftId, [final], now + Duration.FromMinutes(1),
            SurveyInputMethod.Slug, "es", ct);

        (await StoredAnswers(db, draftId, ct)).Should().ContainSingle()
            .Which.Id.Should().Be(final.Id);
        (await db.SurveyResponses.AsNoTracking().SingleAsync(r => r.Id == draftId, ct))
            .SubmittedAt.Should().NotBeNull();
        (await db.SurveyInvitations.AsNoTracking().SingleAsync(i => i.Id == invitationId, ct))
            .Completed.Should().BeTrue();
    }

    private static SurveyAnswer Answer(Guid responseId, Guid questionId, string text) => new()
    {
        Id = Guid.NewGuid(),
        ResponseId = responseId,
        QuestionId = questionId,
        TextValue = text,
    };

    private static async Task<List<SurveyAnswer>> StoredAnswers(
        SurveysDbContext db, Guid responseId, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        return await db.SurveyAnswers.AsNoTracking()
            .Where(answer => answer.ResponseId == responseId)
            .ToListAsync(ct);
    }
}
