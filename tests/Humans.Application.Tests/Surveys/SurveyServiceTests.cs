using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Surveys;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Surveys;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.ValueObjects;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Surveys;

public class SurveyServiceTests
{
    private readonly ISurveyRepository _repo = Substitute.For<ISurveyRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 6, 4, 12, 0));
    private readonly ITeamServiceRead _teamService = Substitute.For<ITeamServiceRead>();
    private readonly IUserServiceRead _userService = Substitute.For<IUserServiceRead>();
    private readonly ITicketServiceRead _ticketService = Substitute.For<ITicketServiceRead>();
    private readonly IShiftView _shiftView = Substitute.For<IShiftView>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly ISurveyInviteTokenProvider _tokenProvider = Substitute.For<ISurveyInviteTokenProvider>();

    private SurveyService CreateService() => new(
        _repo, _audit, _clock, NullLogger<SurveyService>.Instance,
        _teamService, _userService, _ticketService, _shiftView,
        _userEmailService, _emailService, _emailMessages, _tokenProvider);

    private static LocalizedText L(string en) => new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });

    private static OptionInput Opt(string value, string label, int order) => new(null, order, value, L(label));

    private static QuestionInput Q(string prompt, SurveyQuestionType type, int page, int order, params OptionInput[] opts) =>
        new(null, page, order, type, L(prompt), LocalizedText.Empty, false, null, null, LocalizedText.Empty, LocalizedText.Empty, null, opts.ToList());

    private static SurveyEditInput Input(params QuestionInput[] qs) =>
        new(L("Title"), L("Intro"), L("Thanks"), "en", false, null, null, null, null, null, qs.ToList());

    [HumansFact]
    public async Task CreateAsync_persists_draft_with_questions_options_and_creator()
    {
        Survey? captured = null;
        _repo.When(r => r.AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<Survey>());
        var actor = Guid.NewGuid();
        var input = Input(
            Q("Q1", SurveyQuestionType.SingleChoice, 1, 1, Opt("yes", "Yes", 1), Opt("no", "No", 2)),
            Q("Q2", SurveyQuestionType.ShortText, 1, 2));

        var id = await CreateService().CreateAsync(input, actor);

        id.Should().NotBeEmpty();
        captured.Should().NotBeNull();
        captured!.Status.Should().Be(SurveyStatus.Draft);
        captured.CreatedByUserId.Should().Be(actor);
        captured.CreatedAt.Should().Be(_clock.GetCurrentInstant());
        captured.UpdatedAt.Should().Be(_clock.GetCurrentInstant());
        captured.Questions.Should().HaveCount(2);

        var q1 = captured.Questions.Single(q => string.Equals(q.Prompt.Resolve("en", "en"), "Q1", StringComparison.Ordinal));
        q1.Options.Select(o => o.Value).Should().ContainInOrder("yes", "no");
        q1.Options.Should().OnlyContain(o => o.QuestionId == q1.Id);

        await _audit.Received(1).LogAsync(AuditAction.SurveyCreated, "Survey", id, Arg.Any<string>(), actor);
    }

    [HumansFact]
    public async Task UpdateAsync_rejects_forward_reference_and_does_not_persist()
    {
        var q1Id = Guid.NewGuid();
        var q2Id = Guid.NewGuid();
        // q1 (page 1, order 1) shows-if references q2 (page 1, order 2) → forward reference.
        var q1 = new QuestionInput(q1Id, 1, 1, SurveyQuestionType.SingleChoice, L("Q1"), LocalizedText.Empty, false, null, null,
            LocalizedText.Empty, LocalizedText.Empty,
            new BranchCondition { Combine = BranchCombine.All, Clauses = { new BranchClause { QuestionId = q2Id, Operator = BranchOperator.Is, OptionValues = { "yes" } } } },
            new List<OptionInput> { Opt("yes", "Yes", 1) });
        var q2 = new QuestionInput(q2Id, 1, 2, SurveyQuestionType.SingleChoice, L("Q2"), LocalizedText.Empty, false, null, null,
            LocalizedText.Empty, LocalizedText.Empty, null, new List<OptionInput> { Opt("yes", "Yes", 1) });
        var input = new SurveyEditInput(L("T"), L("I"), L("Ty"), "en", false, null, null, null, null, null, new List<QuestionInput> { q1, q2 });

        var act = async () => await CreateService().UpdateAsync(Guid.NewGuid(), input, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task OpenAsync_flips_draft_to_open_and_stamps_updatedAt()
    {
        var id = Guid.NewGuid();
        _repo.GetStatusAsync(id, Arg.Any<CancellationToken>()).Returns(SurveyStatus.Draft);

        await CreateService().OpenAsync(id, Guid.NewGuid());

        await _repo.Received(1).SetStatusAsync(id, SurveyStatus.Open, _clock.GetCurrentInstant(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(AuditAction.SurveyOpened, "Survey", id, Arg.Any<string>(), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task OpenAsync_throws_when_survey_missing()
    {
        _repo.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((SurveyStatus?)null);

        var act = async () => await CreateService().OpenAsync(Guid.NewGuid(), Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Invitations ──────────────────────────────────────────────────────────

    private static Survey SurveyWith(SurveyStatus status, SurveyAudienceType? audience, Guid? teamId) => new()
    {
        Id = Guid.NewGuid(),
        Title = L("My Survey"),
        DefaultCulture = "en",
        Status = status,
        AudienceType = audience,
        AudienceTeamId = teamId,
    };

    private static TeamInfo TeamWith(Guid teamId, params Guid[] memberUserIds) => new(
        teamId, "Team", null, "team",
        IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
        IsPublicPage: false, IsHidden: false, IsPromotedToDirectory: false, Instant.MinValue,
        memberUserIds
            .Select(u => new TeamMemberInfo(Guid.NewGuid(), u, "M", null, null, TeamMemberRole.Member, Instant.MinValue))
            .ToList());

    [HumansFact]
    public async Task PreviewAudienceCountAsync_team_counts_team_members()
    {
        var teamId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Draft, SurveyAudienceType.Team, teamId);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(TeamWith(teamId, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid()));

        var count = await CreateService().PreviewAudienceCountAsync(survey.Id);

        count.Should().Be(3);
    }

    [HumansFact]
    public async Task SendInvitesAsync_creates_invitations_only_for_net_new_recipients()
    {
        var teamId = Guid.NewGuid();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid(), c = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, teamId);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>()).Returns(TeamWith(teamId, a, b, c));
        _repo.GetInvitedUserIdsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid> { a });
        _userEmailService.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>
            {
                [b] = "b@example.org",
                [c] = "c@example.org",
            });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));

        var result = await CreateService().SendInvitesAsync(survey.Id, Guid.NewGuid());

        result.InvitationsCreated.Should().Be(2);
        result.EmailsQueued.Should().Be(2);
        result.Failed.Should().Be(0);
        await _repo.Received(2).AddInvitationAndSaveAsync(Arg.Any<SurveyInvitation>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddInvitationAndSaveAsync(Arg.Is<SurveyInvitation>(i => i.UserId == b), Arg.Any<CancellationToken>());
        await _repo.Received(1).AddInvitationAndSaveAsync(Arg.Is<SurveyInvitation>(i => i.UserId == c), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddInvitationAndSaveAsync(Arg.Is<SurveyInvitation>(i => i.UserId == a), Arg.Any<CancellationToken>());
        await _emailService.Received(2).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(AuditAction.SurveyInvitesSent, "Survey", survey.Id, Arg.Any<string>(), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task SendInvitesAsync_marks_failed_when_email_send_throws()
    {
        var teamId = Guid.NewGuid();
        Guid b = Guid.NewGuid(), c = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, teamId);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>()).Returns(TeamWith(teamId, b, c));
        _repo.GetInvitedUserIdsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns((IReadOnlySet<Guid>)new HashSet<Guid>());
        _userEmailService.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyDictionary<Guid, string>)new Dictionary<Guid, string>
            {
                [b] = "b@example.org",
                [c] = "c@example.org",
            });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));
        // Throw for the first SendAsync call, succeed for the second.
        var calls = 0;
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => calls++ == 0 ? throw new InvalidOperationException("boom") : Task.CompletedTask);

        var result = await CreateService().SendInvitesAsync(survey.Id, Guid.NewGuid());

        result.InvitationsCreated.Should().Be(2);
        result.EmailsQueued.Should().Be(1);
        result.Failed.Should().Be(1);
        await _repo.Received(1).UpdateInvitationStatusAsync(
            Arg.Any<Guid>(), EmailOutboxStatus.Failed, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendInvitesAsync_throws_when_not_open()
    {
        var survey = SurveyWith(SurveyStatus.Draft, SurveyAudienceType.Team, Guid.NewGuid());
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);

        var act = async () => await CreateService().SendInvitesAsync(survey.Id, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task SendInvitesAsync_throws_when_audience_null()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);

        var act = async () => await CreateService().SendInvitesAsync(survey.Id, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task GetInviteStatusesAsync_stitches_burner_names_and_falls_back_to_user_id()
    {
        var surveyId = Guid.NewGuid();
        Guid known = Guid.NewGuid(), unknown = Guid.NewGuid();
        var knownInvite = new SurveyInvitation
        {
            Id = Guid.NewGuid(), SurveyId = surveyId, UserId = known,
            SentAt = _clock.GetCurrentInstant(), LatestEmailStatus = EmailOutboxStatus.Sent,
            Started = true, Completed = true,
        };
        var unknownInvite = new SurveyInvitation
        {
            Id = Guid.NewGuid(), SurveyId = surveyId, UserId = unknown,
            SentAt = _clock.GetCurrentInstant(), LatestEmailStatus = EmailOutboxStatus.Queued,
        };
        _repo.GetInvitationsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SurveyInvitation>)new List<SurveyInvitation> { knownInvite, unknownInvite });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [known] = UserInfoWithName(known, "Sparkle") }));

        var rows = await CreateService().GetInviteStatusesAsync(surveyId);

        rows.Should().HaveCount(2);
        var knownRow = rows.Single(r => r.UserId == known);
        knownRow.Name.Should().Be("Sparkle");
        knownRow.EmailStatus.Should().Be(EmailOutboxStatus.Sent);
        knownRow.Started.Should().BeTrue();
        knownRow.Completed.Should().BeTrue();
        rows.Single(r => r.UserId == unknown).Name.Should().Be(unknown.ToString());
    }

    private static UserInfo UserInfoWithName(Guid id, string burnerName) => new(
        id, burnerName, false, "en", null, Instant.MinValue, null, null, null, null, null,
        false, null, false, null, GoogleEmailStatus.Unknown, null, null, null, null, null,
        [], [], [], null, []);

    // ── Answering (wizard entry) ───────────────────────────────────────────────

    private static SurveyInvitation InvitationFor(Guid surveyId, Guid userId) => new()
    {
        Id = Guid.NewGuid(),
        SurveyId = surveyId,
        UserId = userId,
        CreatedAt = Instant.MinValue,
    };

    [HumansFact]
    public async Task ResolveAnswerContextAsync_returns_null_for_invalid_token()
    {
        _tokenProvider.Resolve("bad").Returns((Guid?)null);

        var ctx = await CreateService().ResolveAnswerContextAsync("bad");

        ctx.Should().BeNull();
        await _repo.DidNotReceive().GetInvitationByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResolveAnswerContextAsync_populates_context_and_flags_resumable_draft()
    {
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, Guid.NewGuid());
        var userId = Guid.NewGuid();
        var invitation = InvitationFor(survey.Id, userId);
        var questionId = Guid.NewGuid();
        var draft = new SurveyResponse
        {
            Id = Guid.NewGuid(),
            SurveyId = survey.Id,
            UserId = userId,
            InvitationId = invitation.Id,
            Anonymity = ResponseAnonymity.Identified,
            Answers = new List<SurveyAnswer>
            {
                new() { Id = Guid.NewGuid(), QuestionId = questionId, SelectedOptionValues = ["yes"], TextValue = "note", RatingValue = 4 },
            },
        };

        _tokenProvider.Resolve("good").Returns(invitation.Id);
        _repo.GetInvitationByIdAsync(invitation.Id, Arg.Any<CancellationToken>()).Returns(invitation);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _repo.GetDraftResponseAsync(survey.Id, userId, Arg.Any<CancellationToken>()).Returns(draft);

        var ctx = await CreateService().ResolveAnswerContextAsync("good");

        ctx.Should().NotBeNull();
        ctx!.SurveyId.Should().Be(survey.Id);
        ctx.InvitationId.Should().Be(invitation.Id);
        ctx.UserId.Should().Be(userId);
        ctx.HasResumableDraft.Should().BeTrue();
        ctx.Definition.Status.Should().Be(SurveyStatus.Open);
        ctx.DraftAnswers.Should().ContainSingle();
        var answer = ctx.DraftAnswers[0];
        answer.QuestionId.Should().Be(questionId);
        answer.SelectedOptionValues.Should().ContainInOrder("yes");
        answer.TextValue.Should().Be("note");
        answer.RatingValue.Should().Be(4);
    }

    [HumansFact]
    public async Task ResolveAnswerContextAsync_returns_null_when_invitation_missing()
    {
        var invitationId = Guid.NewGuid();
        _tokenProvider.Resolve("orphan").Returns(invitationId);
        _repo.GetInvitationByIdAsync(invitationId, Arg.Any<CancellationToken>()).Returns((SurveyInvitation?)null);

        var ctx = await CreateService().ResolveAnswerContextAsync("orphan");

        ctx.Should().BeNull();
    }

    [HumansFact]
    public async Task StartIdentifiedDraftAsync_returns_existing_draft_without_creating_a_second()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var existing = new SurveyResponse
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            UserId = userId,
            Anonymity = ResponseAnonymity.Identified,
        };
        _repo.GetDraftResponseAsync(surveyId, userId, Arg.Any<CancellationToken>()).Returns(existing);

        var id = await CreateService().StartIdentifiedDraftAsync(surveyId, Guid.NewGuid(), userId, "en");

        id.Should().Be(existing.Id);
        await _repo.DidNotReceive().AddResponseAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task StartIdentifiedDraftAsync_creates_identified_draft_when_none_exists()
    {
        var surveyId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        SurveyResponse? captured = null;
        _repo.GetDraftResponseAsync(surveyId, userId, Arg.Any<CancellationToken>()).Returns((SurveyResponse?)null);
        _repo.When(r => r.AddResponseAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<SurveyResponse>());

        var id = await CreateService().StartIdentifiedDraftAsync(surveyId, invitationId, userId, "es");

        captured.Should().NotBeNull();
        captured!.Id.Should().Be(id);
        captured.SurveyId.Should().Be(surveyId);
        captured.InvitationId.Should().Be(invitationId);
        captured.UserId.Should().Be(userId);
        captured.Anonymity.Should().Be(ResponseAnonymity.Identified);
        captured.InputMethod.Should().Be(SurveyInputMethod.UserSpecificLink);
        captured.Culture.Should().Be("es");
        captured.SubmittedAt.Should().BeNull();
        await _repo.Received(1).AddResponseAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }
}
