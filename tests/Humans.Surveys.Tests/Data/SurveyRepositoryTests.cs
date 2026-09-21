using AwesomeAssertions;
using Humans.Surveys.Contracts;
using Humans.Surveys.Data;
using Humans.Surveys.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Surveys.Tests.Data;

public sealed class SurveyRepositoryTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 9, 21, 6, 0);
    private readonly SurveysDbContext _db;
    private readonly SurveyRepository _repository;

    public SurveyRepositoryTests()
    {
        var options = new DbContextOptionsBuilder<SurveysDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new SurveysDbContext(options);
        _repository = new SurveyRepository(new TestDbContextFactory<SurveysDbContext>(options));
    }

    public void Dispose() => _db.Dispose();

    [HumansFact]
    public async Task GetInvitationsDueForReminderAsync_returns_only_sent_open_uncompleted_unreminded_invitees()
    {
        var openSurvey = Survey(SurveyStatus.Open);
        var draftSurvey = Survey(SurveyStatus.Draft);
        await _db.Surveys.AddRangeAsync([openSurvey, draftSurvey], Xunit.TestContext.Current.CancellationToken);
        var due = Invitation(openSurvey.Id, sentAt: Now - Duration.FromDays(7));
        var unsent = Invitation(openSurvey.Id, sentAt: null);
        var alreadyReminded = Invitation(openSurvey.Id, sentAt: Now - Duration.FromDays(7), reminderSentAt: Now);
        var completed = Invitation(openSurvey.Id, sentAt: Now - Duration.FromDays(7), completed: true);
        var tooRecent = Invitation(openSurvey.Id, sentAt: Now - Duration.FromDays(6));
        var closed = Invitation(draftSurvey.Id, sentAt: Now - Duration.FromDays(7));
        await _db.SurveyInvitations.AddRangeAsync(
            [due, unsent, alreadyReminded, completed, tooRecent, closed], Xunit.TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        var invitations = await _repository.GetInvitationsDueForReminderAsync(
            Now - Duration.FromDays(7), Xunit.TestContext.Current.CancellationToken);

        invitations.Should().ContainSingle().Which.Id.Should().Be(due.Id);
    }

    [HumansFact]
    public async Task SetReminderSentAsync_stamps_the_invitation_without_changing_delivery_state()
    {
        var survey = Survey(SurveyStatus.Open);
        var invitation = Invitation(survey.Id, sentAt: Now - Duration.FromDays(7));
        invitation.LatestEmailStatus = Humans.Base.Enums.EmailOutboxStatus.Queued;
        await _db.Surveys.AddAsync(survey, Xunit.TestContext.Current.CancellationToken);
        await _db.SurveyInvitations.AddAsync(invitation, Xunit.TestContext.Current.CancellationToken);
        await _db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);

        await _repository.SetReminderSentAsync(invitation.Id, Now, Xunit.TestContext.Current.CancellationToken);

        _db.ChangeTracker.Clear();
        var persisted = await _db.SurveyInvitations.SingleAsync(item => item.Id == invitation.Id,
            Xunit.TestContext.Current.CancellationToken);
        persisted.ReminderSentAt.Should().Be(Now);
        persisted.SentAt.Should().Be(Now - Duration.FromDays(7));
        persisted.LatestEmailStatus.Should().Be(Humans.Base.Enums.EmailOutboxStatus.Queued);
    }

    private static Survey Survey(SurveyStatus status) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        CreatedByUserId = Guid.NewGuid(),
        CreatedAt = Now,
        UpdatedAt = Now
    };

    private static SurveyInvitation Invitation(
        Guid surveyId, Instant? sentAt, Instant? reminderSentAt = null, bool completed = false) => new()
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            UserId = Guid.NewGuid(),
            SentAt = sentAt,
            ReminderSentAt = reminderSentAt,
            Completed = completed,
            CreatedAt = Now
        };
}
