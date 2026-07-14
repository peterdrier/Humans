using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.GoogleIntegration;
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
    private readonly IGoogleTranslationService _translation = Substitute.For<IGoogleTranslationService>();

    private SurveyService CreateService() => new(
        _repo, _audit, _clock, NullLogger<SurveyService>.Instance,
        _teamService, _userService, _ticketService, _shiftView,
        _userEmailService, _emailService, _emailMessages, _tokenProvider, _translation);

    private static LocalizedText L(string en) => new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });

    private static OptionInput Opt(string value, string label, int order) => new(null, order, value, L(label));

    private static QuestionInput Q(string prompt, SurveyQuestionType type, int page, int order, params OptionInput[] opts) =>
        new(null, page, order, type, L(prompt), LocalizedText.Empty, false, null, null, LocalizedText.Empty, LocalizedText.Empty, null, opts.ToList());

    private static SurveyEditInput Input(params QuestionInput[] qs) =>
        new(L("Title"), L("Intro"), L("Thanks"), "en", false, null, null, null, null, null, null, qs.ToList());

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

        var id = await CreateService().CreateAsync(input, actor, TestContext.Current.CancellationToken);

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

    private static SurveyEditInput InputWithSlug(string? slug) =>
        new(L("Title"), L("Intro"), L("Thanks"), "en", true, null, null, null, null, null, slug, []);

    [HumansTheory]
    [InlineData("admin")]
    [InlineData("Admin")]
    [InlineData("ANSWER")]
    [InlineData(" answer ")]
    public async Task CreateAsync_rejects_reserved_slug_and_does_not_persist(string slug)
    {
        var act = async () => await CreateService().CreateAsync(InputWithSlug(slug), Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _repo.DidNotReceive().AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("admin")]
    [InlineData("Admin")]
    [InlineData("ANSWER")]
    public async Task UpdateAsync_rejects_reserved_slug_and_does_not_persist(string slug)
    {
        var act = async () => await CreateService().UpdateAsync(Guid.NewGuid(), InputWithSlug(slug), Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateAsync_accepts_non_reserved_slug_and_normalises_it()
    {
        Survey? captured = null;
        _repo.When(r => r.AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<Survey>());

        await CreateService().CreateAsync(InputWithSlug(" Summer-Feedback "), Guid.NewGuid(), TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.PublicSlug.Should().Be("summer-feedback");
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
        var input = new SurveyEditInput(L("T"), L("I"), L("Ty"), "en", false, null, null, null, null, null, null, new List<QuestionInput> { q1, q2 });

        var act = async () => await CreateService().UpdateAsync(Guid.NewGuid(), input, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task OpenAsync_flips_draft_to_open_and_stamps_updatedAt()
    {
        var id = Guid.NewGuid();
        _repo.GetStatusAsync(id, Arg.Any<CancellationToken>()).Returns(SurveyStatus.Draft);

        await CreateService().OpenAsync(id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _repo.Received(1).SetStatusAsync(id, SurveyStatus.Open, _clock.GetCurrentInstant(), Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(AuditAction.SurveyOpened, "Survey", id, Arg.Any<string>(), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task OpenAsync_throws_when_survey_missing()
    {
        _repo.GetStatusAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((SurveyStatus?)null);

        var act = async () => await CreateService().OpenAsync(Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Pre-fill translations (§6.1) ─────────────────────────────────────────

    private void StubTranslationAsMarker() =>
        _translation.TranslateAsync(Arg.Any<IReadOnlyList<string>>(), "en", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult<IReadOnlyList<string>>(
                ci.Arg<IReadOnlyList<string>>().Select(t => ci.ArgAt<string>(2) + ":" + t).ToList()));

    [HumansFact]
    public async Task PreFillTranslationsAsync_fills_only_blank_cultures_and_never_overwrites()
    {
        var survey = SurveyWith(SurveyStatus.Draft, null, null);
        survey.Title = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = "Hello",
            ["es"] = "Hola", // authored — must survive untouched
        });
        var questionId = Guid.NewGuid();
        survey.Questions = new List<SurveyQuestion>
        {
            new()
            {
                Id = questionId, SurveyId = survey.Id, PageNumber = 1, Order = 1,
                Type = SurveyQuestionType.SingleChoice, Prompt = L("How was it?"),
                Options = new List<SurveyQuestionOption>
                {
                    new() { Id = Guid.NewGuid(), QuestionId = questionId, Order = 1, Value = "good", Label = L("Good") },
                },
            },
        };
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        Survey? captured = null;
        _repo.When(r => r.UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<Survey>());
        StubTranslationAsMarker();

        // es is missing prompt+label (2); de is missing title+prompt+label (3).
        var filled = await CreateService().PreFillTranslationsAsync(survey.Id, ["en", "es", "de"], Guid.NewGuid(), TestContext.Current.CancellationToken);

        filled.Should().Be(5);
        captured.Should().NotBeNull();
        captured!.Title.Values["es"].Should().Be("Hola");
        captured.Title.Values["de"].Should().Be("de:Hello");
        var q = captured.Questions.Single();
        q.Prompt.Values["es"].Should().Be("es:How was it?");
        q.Prompt.Values["de"].Should().Be("de:How was it?");
        q.Options.Single().Label.Values["es"].Should().Be("es:Good");
        // Empty source fields (intro, thank-you, help, rating labels) are not sent for translation.
        captured.Intro.Values.Should().NotContainKey("de");
    }

    [HumansFact]
    public async Task PreFillTranslationsAsync_no_missing_translations_is_a_noop()
    {
        var survey = SurveyWith(SurveyStatus.Draft, null, null);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);

        // Only target is the source culture itself → nothing to fill.
        var filled = await CreateService().PreFillTranslationsAsync(survey.Id, ["en"], Guid.NewGuid(), TestContext.Current.CancellationToken);

        filled.Should().Be(0);
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
        await _translation.DidNotReceive().TranslateAsync(
            Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Invitations ──────────────────────────────────────────────────────────

    private static Survey SurveyWith(SurveyStatus status, SurveyAudienceType? audience, Guid? teamId, Instant? loggedInSince = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = L("My Survey"),
        DefaultCulture = "en",
        Status = status,
        AudienceType = audience,
        AudienceTeamId = teamId,
        AudienceLoggedInSince = loggedInSince,
    };

    private static UserInfo UserWithLastLogin(Guid id, Instant? lastLogin, UserState? state = null) =>
        UserInfo.Create(
            new User { Id = id, PreferredLanguage = "en", LastLoginAt = lastLogin, State = state },
            [], [], [], null, [], [], [], []);

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

        var count = await CreateService().PreviewAudienceCountAsync(survey.Id, TestContext.Current.CancellationToken);

        count.Should().Be(3);
    }

    [HumansFact]
    public async Task PreviewAudienceCountAsync_loggedInSince_counts_only_users_logged_in_at_or_after_cutoff()
    {
        var cutoff = Instant.FromUtc(2026, 1, 1, 0, 0);
        var survey = SurveyWith(SurveyStatus.Draft, SurveyAudienceType.LoggedInSince, null, cutoff);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new List<UserInfo>
        {
            UserWithLastLogin(Guid.NewGuid(), cutoff - Duration.FromDays(1)),  // before → excluded
            UserWithLastLogin(Guid.NewGuid(), cutoff),                         // exactly at → included
            UserWithLastLogin(Guid.NewGuid(), cutoff + Duration.FromDays(30)), // after → included
            UserWithLastLogin(Guid.NewGuid(), null),                           // never logged in → excluded
        });

        var count = await CreateService().PreviewAudienceCountAsync(survey.Id, TestContext.Current.CancellationToken);

        count.Should().Be(2);
    }

    [HumansFact]
    public async Task PreviewAudienceCountAsync_loggedInSince_excludes_status_walled_accounts()
    {
        var cutoff = Instant.FromUtc(2026, 1, 1, 0, 0);
        var recentLogin = cutoff + Duration.FromDays(3);
        var survey = SurveyWith(SurveyStatus.Draft, SurveyAudienceType.LoggedInSince, null, cutoff);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new List<UserInfo>
        {
            UserWithLastLogin(Guid.NewGuid(), recentLogin, UserState.Active),         // included
            UserWithLastLogin(Guid.NewGuid(), recentLogin, UserState.Bare),           // mid-onboarding → included
            UserWithLastLogin(Guid.NewGuid(), recentLogin),                           // legacy null state → included
            UserWithLastLogin(Guid.NewGuid(), recentLogin, UserState.Rejected),       // status wall → excluded
            UserWithLastLogin(Guid.NewGuid(), recentLogin, UserState.Suspended),      // status wall → excluded
            UserWithLastLogin(Guid.NewGuid(), recentLogin, UserState.AdminSuspended), // status wall → excluded
        });

        var count = await CreateService().PreviewAudienceCountAsync(survey.Id, TestContext.Current.CancellationToken);

        count.Should().Be(3);
    }

    [HumansFact]
    public async Task PreviewAudienceCountAsync_loggedInSince_returns_zero_when_cutoff_missing()
    {
        var survey = SurveyWith(SurveyStatus.Draft, SurveyAudienceType.LoggedInSince, null);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(new List<UserInfo> { UserWithLastLogin(Guid.NewGuid(), Instant.FromUtc(2026, 6, 1, 0, 0)) });

        var count = await CreateService().PreviewAudienceCountAsync(survey.Id, TestContext.Current.CancellationToken);

        count.Should().Be(0);
    }

    [HumansFact]
    public async Task SendInvitesAsync_loggedInSince_invites_only_matching_users()
    {
        var cutoff = Instant.FromUtc(2026, 1, 1, 0, 0);
        Guid recent = Guid.NewGuid(), stale = Guid.NewGuid(), never = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.LoggedInSince, null, cutoff);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _repo.GetInvitedUserIdsAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(new HashSet<Guid>());
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(new List<UserInfo>
        {
            UserWithLastLogin(recent, cutoff + Duration.FromDays(3)),
            UserWithLastLogin(stale, cutoff - Duration.FromDays(3)),
            UserWithLastLogin(never, null),
        });
        _userEmailService.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>
            {
                [recent] = "recent@example.org",
                [stale] = "stale@example.org",
                [never] = "never@example.org",
            });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));

        var result = await CreateService().SendInvitesAsync(survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.InvitationsCreated.Should().Be(1);
        result.EmailsQueued.Should().Be(1);
        await _repo.Received(1).AddInvitationAndSaveAsync(Arg.Is<SurveyInvitation>(i => i.UserId == recent), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddInvitationAndSaveAsync(Arg.Is<SurveyInvitation>(i => i.UserId == stale), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddInvitationAndSaveAsync(Arg.Is<SurveyInvitation>(i => i.UserId == never), Arg.Any<CancellationToken>());
        _tokenProvider.Received(1).Create(Arg.Any<Guid>());
        await _emailService.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
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
            .Returns(new HashSet<Guid> { a });
        _userEmailService.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>
            {
                [b] = "b@example.org",
                [c] = "c@example.org",
            });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));

        var result = await CreateService().SendInvitesAsync(survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

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
            .Returns(new HashSet<Guid>());
        _userEmailService.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>
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

        var result = await CreateService().SendInvitesAsync(survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

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

        var act = async () => await CreateService().SendInvitesAsync(survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task SendInvitesAsync_throws_when_audience_null()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);

        var act = async () => await CreateService().SendInvitesAsync(survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Reminders (7-day nudge) ────────────────────────────────────────────────

    [HumansFact]
    public async Task SendDueRemindersAsync_sends_one_reminder_stamps_reminder_and_returns_count()
    {
        var now = _clock.GetCurrentInstant();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, Guid.NewGuid());
        var userId = Guid.NewGuid();
        var inv = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = survey.Id,
            UserId = userId,
            SentAt = now - Duration.FromDays(8),
            LatestEmailStatus = EmailOutboxStatus.Sent,
        };
        _repo.GetInvitationsDueForReminderAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(new List<SurveyInvitation> { inv });
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _userEmailService.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "u@example.org" });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = UserInfoWithName(userId, "Sparkle") }));

        var count = await CreateService().SendDueRemindersAsync(TestContext.Current.CancellationToken);

        count.Should().Be(1);
        await _emailService.Received(1).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        _emailMessages.Received(1).SurveyReminder("u@example.org", "Sparkle", Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>());
        await _repo.Received(1).SetReminderSentAsync(inv.Id, now, Arg.Any<CancellationToken>());
        await _audit.Received(1).LogAsync(
            AuditAction.SurveyReminderSent, "Survey", Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task SendDueRemindersAsync_returns_zero_and_sends_nothing_when_none_due()
    {
        _repo.GetInvitationsDueForReminderAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(new List<SurveyInvitation>());

        var count = await CreateService().SendDueRemindersAsync(TestContext.Current.CancellationToken);

        count.Should().Be(0);
        await _emailService.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SetReminderSentAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendDueRemindersAsync_skips_invitee_with_no_resolvable_email()
    {
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, Guid.NewGuid());
        var userId = Guid.NewGuid();
        var inv = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = survey.Id,
            UserId = userId,
            SentAt = _clock.GetCurrentInstant() - Duration.FromDays(9),
            LatestEmailStatus = EmailOutboxStatus.Sent,
        };
        _repo.GetInvitationsDueForReminderAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(new List<SurveyInvitation> { inv });
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _userEmailService.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var count = await CreateService().SendDueRemindersAsync(TestContext.Current.CancellationToken);

        count.Should().Be(0);
        await _emailService.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SetReminderSentAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendDueRemindersAsync_continues_sweep_after_one_send_failure()
    {
        var now = _clock.GetCurrentInstant();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, Guid.NewGuid());
        Guid userA = Guid.NewGuid(), userB = Guid.NewGuid();
        var invA = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = survey.Id,
            UserId = userA,
            SentAt = now - Duration.FromDays(8),
            LatestEmailStatus = EmailOutboxStatus.Sent,
        };
        var invB = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = survey.Id,
            UserId = userB,
            SentAt = now - Duration.FromDays(8),
            LatestEmailStatus = EmailOutboxStatus.Sent,
        };
        _repo.GetInvitationsDueForReminderAsync(Arg.Any<Instant>(), Arg.Any<CancellationToken>())
            .Returns(new List<SurveyInvitation> { invA, invB });
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _userEmailService.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>
            {
                [userA] = "a@example.org",
                [userB] = "b@example.org",
            });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));
        // First send (invitee A) blows up; the sweep must still reach invitee B.
        _emailService.SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>())
            .Returns(
                _ => Task.FromException(new InvalidOperationException("smtp down")),
                _ => Task.CompletedTask);

        var count = await CreateService().SendDueRemindersAsync(TestContext.Current.CancellationToken);

        count.Should().Be(1);
        await _emailService.Received(2).SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        // A stays unstamped (retried next run); B is stamped.
        await _repo.DidNotReceive().SetReminderSentAsync(invA.Id, Arg.Any<Instant>(), Arg.Any<CancellationToken>());
        await _repo.Received(1).SetReminderSentAsync(invB.Id, now, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetInviteStatusesAsync_stitches_burner_names_and_falls_back_to_user_id()
    {
        var surveyId = Guid.NewGuid();
        Guid known = Guid.NewGuid(), unknown = Guid.NewGuid();
        var knownInvite = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            UserId = known,
            SentAt = _clock.GetCurrentInstant(),
            LatestEmailStatus = EmailOutboxStatus.Sent,
            Started = true,
            Completed = true,
        };
        var unknownInvite = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            UserId = unknown,
            SentAt = _clock.GetCurrentInstant(),
            LatestEmailStatus = EmailOutboxStatus.Queued,
        };
        _repo.GetInvitationsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new List<SurveyInvitation> { knownInvite, unknownInvite });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [known] = UserInfoWithName(known, "Sparkle") }));

        var rows = await CreateService().GetInviteStatusesAsync(surveyId, TestContext.Current.CancellationToken);

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
        false, null, false, null, null, null, null, null, null,
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

        var ctx = await CreateService().ResolveAnswerContextAsync("bad", TestContext.Current.CancellationToken);

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

        var ctx = await CreateService().ResolveAnswerContextAsync("good", TestContext.Current.CancellationToken);

        ctx.Should().NotBeNull();
        ctx.SurveyId.Should().Be(survey.Id);
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

        var ctx = await CreateService().ResolveAnswerContextAsync("orphan", TestContext.Current.CancellationToken);

        ctx.Should().BeNull();
    }

    [HumansFact]
    public async Task ResolveAnswerContextAsync_returns_null_when_invitation_completed()
    {
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, Guid.NewGuid());
        var invitation = InvitationFor(survey.Id, Guid.NewGuid());
        invitation.Completed = true;
        _tokenProvider.Resolve("spent").Returns(invitation.Id);
        _repo.GetInvitationByIdAsync(invitation.Id, Arg.Any<CancellationToken>()).Returns(invitation);

        var ctx = await CreateService().ResolveAnswerContextAsync("spent", TestContext.Current.CancellationToken);

        ctx.Should().BeNull();
        await _repo.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResolvePublicContextAsync_returns_null_for_unknown_slug()
    {
        _repo.GetIdByPublicSlugAsync("missing", Arg.Any<CancellationToken>()).Returns((Guid?)null);

        var ctx = await CreateService().ResolvePublicContextAsync("MISSING", TestContext.Current.CancellationToken);

        ctx.Should().BeNull();
        // Lookup uses the normalised (lower-cased/trimmed) slug.
        await _repo.Received(1).GetIdByPublicSlugAsync("missing", Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResolvePublicContextAsync_returns_null_for_blank_slug()
    {
        var ctx = await CreateService().ResolvePublicContextAsync("   ", TestContext.Current.CancellationToken);

        ctx.Should().BeNull();
        await _repo.DidNotReceive().GetIdByPublicSlugAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ResolvePublicContextAsync_returns_context_for_known_slug()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        survey.PublicSlug = "feedback";
        survey.AllowAnonymous = true;
        _repo.GetIdByPublicSlugAsync("feedback", Arg.Any<CancellationToken>()).Returns(survey.Id);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);

        var ctx = await CreateService().ResolvePublicContextAsync(" Feedback ", TestContext.Current.CancellationToken);

        ctx.Should().NotBeNull();
        ctx.SurveyId.Should().Be(survey.Id);
        ctx.Definition.Id.Should().Be(survey.Id);
        ctx.Definition.Status.Should().Be(SurveyStatus.Open);
    }

    [HumansFact]
    public async Task ResolvePublicContextAsync_returns_null_when_anonymous_disallowed()
    {
        // A slug left behind after AllowAnonymous was switched off must not resolve —
        // the service is the authoritative guard, not just the controller.
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        survey.PublicSlug = "feedback";
        survey.AllowAnonymous = false;
        _repo.GetIdByPublicSlugAsync("feedback", Arg.Any<CancellationToken>()).Returns(survey.Id);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);

        var ctx = await CreateService().ResolvePublicContextAsync("feedback", TestContext.Current.CancellationToken);

        ctx.Should().BeNull();
    }

    [HumansFact]
    public async Task IncrementPublicStartedAsync_delegates_to_repo_once()
    {
        var surveyId = Guid.NewGuid();

        await CreateService().IncrementPublicStartedAsync(surveyId, TestContext.Current.CancellationToken);

        await _repo.Received(1).IncrementPublicStartedAsync(surveyId, Arg.Any<CancellationToken>());
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

        var id = await CreateService().StartIdentifiedDraftAsync(surveyId, Guid.NewGuid(), userId, "en", TestContext.Current.CancellationToken);

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

        var id = await CreateService().StartIdentifiedDraftAsync(surveyId, invitationId, userId, "es", TestContext.Current.CancellationToken);

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

    // ── Submit (anonymity encoding) ────────────────────────────────────────────

    private Survey SurveyForSubmit(out Guid q1Id, out Guid q2Id)
    {
        q1Id = Guid.NewGuid();
        q2Id = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, Guid.NewGuid());
        survey.Questions = new List<SurveyQuestion>
        {
            new() { Id = q1Id, SurveyId = survey.Id, PageNumber = 1, Order = 1, Type = SurveyQuestionType.SingleChoice },
            new() { Id = q2Id, SurveyId = survey.Id, PageNumber = 1, Order = 2, Type = SurveyQuestionType.ShortText },
        };
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        return survey;
    }

    private static SurveyAnswerInput Ans(Guid q, params string[] options) => new(q, options.ToList(), null, null);
    private static SurveyAnswerInput TextAns(Guid q, string text) => new(q, [], text, null);

    [HumansFact]
    public async Task SubmitResponseAsync_identified_finalises_existing_draft_and_completes_invitation()
    {
        var survey = SurveyForSubmit(out var q1Id, out var q2Id);
        var draftId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        var submission = new SurveySubmission(
            survey.Id, invitationId, Guid.NewGuid(), draftId,
            ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink, "en",
            new List<SurveyAnswerInput> { Ans(q1Id, "yes"), TextAns(q2Id, "note") });

        await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        await _repo.Received(1).SaveDraftAnswersAsync(
            draftId,
            Arg.Is<IReadOnlyList<SurveyAnswer>>(a => a.Count == 2),
            Arg.Is<Instant?>(t => t == _clock.GetCurrentInstant()),
            Arg.Any<CancellationToken>());
        await _repo.Received(1).SetInvitationCompletedAsync(invitationId, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitResponseAsync_identified_without_draft_creates_linked_response()
    {
        var survey = SurveyForSubmit(out var q1Id, out _);
        var userId = Guid.NewGuid();
        var invitationId = Guid.NewGuid();
        SurveyResponse? captured = null;
        _repo.When(r => r.AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<SurveyResponse>());
        var submission = new SurveySubmission(
            survey.Id, invitationId, userId, null,
            ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink, "en",
            new List<SurveyAnswerInput> { Ans(q1Id, "yes") });

        await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.UserId.Should().Be(userId);
        captured.InvitationId.Should().Be(invitationId);
        captured.Anonymity.Should().Be(ResponseAnonymity.Identified);
        captured.SubmittedAt.Should().Be(_clock.GetCurrentInstant());
        await _repo.Received(1).SetInvitationCompletedAsync(invitationId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitResponseAsync_completion_tracked_stores_unlinked_response_and_completes_invitation()
    {
        var survey = SurveyForSubmit(out var q1Id, out _);
        var invitationId = Guid.NewGuid();
        SurveyResponse? captured = null;
        _repo.When(r => r.AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<SurveyResponse>());
        var submission = new SurveySubmission(
            survey.Id, invitationId, null, null,
            ResponseAnonymity.CompletionTracked, SurveyInputMethod.UserSpecificLink, "en",
            new List<SurveyAnswerInput> { Ans(q1Id, "yes") });

        await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.UserId.Should().BeNull();
        captured.InvitationId.Should().BeNull();
        captured.Anonymity.Should().Be(ResponseAnonymity.CompletionTracked);
        captured.SubmittedAt.Should().Be(_clock.GetCurrentInstant());
        await _repo.Received(1).SetInvitationCompletedAsync(invitationId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitResponseAsync_anonymous_stores_unlinked_response_and_leaves_invitation_untouched()
    {
        var survey = SurveyForSubmit(out var q1Id, out _);
        SurveyResponse? captured = null;
        _repo.When(r => r.AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<SurveyResponse>());
        var submission = new SurveySubmission(
            survey.Id, Guid.NewGuid(), null, null,
            ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en",
            new List<SurveyAnswerInput> { Ans(q1Id, "yes") });

        await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.UserId.Should().BeNull();
        captured.InvitationId.Should().BeNull();
        captured.Anonymity.Should().Be(ResponseAnonymity.Anonymous);
        await _repo.DidNotReceive().SetInvitationCompletedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitResponseAsync_drops_answers_to_questions_hidden_by_branching()
    {
        var gate = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, Guid.NewGuid());
        survey.Questions = new List<SurveyQuestion>
        {
            new() { Id = gate, SurveyId = survey.Id, PageNumber = 1, Order = 1, Type = SurveyQuestionType.SingleChoice },
            // visible only when gate == "yes"
            new()
            {
                Id = hidden, SurveyId = survey.Id, PageNumber = 2, Order = 1, Type = SurveyQuestionType.ShortText,
                ShowIf = new BranchCondition
                {
                    Combine = BranchCombine.All,
                    Clauses = { new BranchClause { QuestionId = gate, Operator = BranchOperator.Is, OptionValues = { "yes" } } },
                },
            },
        };
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        SurveyResponse? captured = null;
        _repo.When(r => r.AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<SurveyResponse>());

        // gate answered "no" → the hidden question's stale answer must be dropped.
        var submission = new SurveySubmission(
            survey.Id, null, null, null,
            ResponseAnonymity.Anonymous, SurveyInputMethod.UserSpecificLink, "en",
            new List<SurveyAnswerInput> { Ans(gate, "no"), TextAns(hidden, "leaked") });

        await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.Answers.Select(a => a.QuestionId).Should().Contain(gate);
        captured.Answers.Select(a => a.QuestionId).Should().NotContain(hidden);
    }

    [HumansFact]
    public async Task SubmitResponseAsync_drops_answers_unlocked_only_by_a_stale_hidden_answer()
    {
        // Q1 gates Q2, Q2 gates Q3 — flipping Q1 to "no" must hide Q3 even though Q2's stale
        // answer would still satisfy Q3's condition on its own.
        var q1 = Guid.NewGuid();
        var q2 = Guid.NewGuid();
        var q3 = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, Guid.NewGuid());
        survey.Questions = new List<SurveyQuestion>
        {
            new() { Id = q1, SurveyId = survey.Id, PageNumber = 1, Order = 1, Type = SurveyQuestionType.SingleChoice },
            new()
            {
                Id = q2, SurveyId = survey.Id, PageNumber = 2, Order = 1, Type = SurveyQuestionType.SingleChoice,
                ShowIf = new BranchCondition
                {
                    Combine = BranchCombine.All,
                    Clauses = { new BranchClause { QuestionId = q1, Operator = BranchOperator.Is, OptionValues = { "yes" } } },
                },
            },
            new()
            {
                Id = q3, SurveyId = survey.Id, PageNumber = 3, Order = 1, Type = SurveyQuestionType.ShortText,
                ShowIf = new BranchCondition
                {
                    Combine = BranchCombine.All,
                    Clauses = { new BranchClause { QuestionId = q2, Operator = BranchOperator.Is, OptionValues = { "vegetarian" } } },
                },
            },
        };
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        SurveyResponse? captured = null;
        _repo.When(r => r.AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<SurveyResponse>());

        var submission = new SurveySubmission(
            survey.Id, null, null, null,
            ResponseAnonymity.Anonymous, SurveyInputMethod.UserSpecificLink, "en",
            new List<SurveyAnswerInput> { Ans(q1, "no"), Ans(q2, "vegetarian"), TextAns(q3, "leaked") });

        await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.Answers.Select(a => a.QuestionId).Should().BeEquivalentTo([q1]);
    }

    [HumansFact]
    public async Task SubmitResponseAsync_throws_when_invitation_already_completed()
    {
        var survey = SurveyForSubmit(out var q1Id, out _);
        var invitation = InvitationFor(survey.Id, Guid.NewGuid());
        invitation.Completed = true;
        _repo.GetInvitationByIdAsync(invitation.Id, Arg.Any<CancellationToken>()).Returns(invitation);
        var submission = new SurveySubmission(
            survey.Id, invitation.Id, null, null,
            ResponseAnonymity.CompletionTracked, SurveyInputMethod.UserSpecificLink, "en",
            new List<SurveyAnswerInput> { Ans(q1Id, "yes") });

        var act = async () => await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SetInvitationCompletedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitResponseAsync_throws_when_survey_not_open()
    {
        var survey = SurveyForSubmit(out var q1Id, out _);
        survey.Status = SurveyStatus.Closed;
        var submission = new SurveySubmission(
            survey.Id, null, null, null,
            ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en",
            new List<SurveyAnswerInput> { Ans(q1Id, "yes") });

        var act = async () => await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitResponseAsync_throws_when_window_closed()
    {
        var survey = SurveyForSubmit(out var q1Id, out _);
        survey.ClosesAt = _clock.GetCurrentInstant() - Duration.FromMinutes(1);
        var submission = new SurveySubmission(
            survey.Id, null, null, null,
            ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en",
            new List<SurveyAnswerInput> { Ans(q1Id, "yes") });

        var act = async () => await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }

    // ── Wizard advance (flow decisions live in the service) ────────────────────

    private Survey SurveyForWizard(out Guid q1Id, out Guid q2Id)
    {
        q1Id = Guid.NewGuid();
        q2Id = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        survey.Questions = new List<SurveyQuestion>
        {
            new()
            {
                Id = q1Id, SurveyId = survey.Id, PageNumber = 1, Order = 1,
                Type = SurveyQuestionType.SingleChoice, IsRequired = true, Prompt = L("Q1"),
                Options =
                [
                    new SurveyQuestionOption { Id = Guid.NewGuid(), QuestionId = q1Id, Order = 1, Value = "yes", Label = L("Yes") },
                ],
            },
            new()
            {
                Id = q2Id, SurveyId = survey.Id, PageNumber = 2, Order = 1,
                Type = SurveyQuestionType.ShortText, Prompt = L("Q2"),
            },
        };
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        return survey;
    }

    private static SurveyWizardState WizardState(Guid surveyId, Guid? invitationId = null) => new()
    {
        SurveyId = surveyId,
        InvitationId = invitationId,
        Anonymity = ResponseAnonymity.Anonymous,
        InputMethod = SurveyInputMethod.UserSpecificLink,
        Culture = "en",
        CurrentPage = 1,
    };

    [HumansFact]
    public async Task AdvanceWizardAsync_navigates_to_next_page_and_fires_started_once()
    {
        var survey = SurveyForWizard(out var q1Id, out _);
        var invitationId = Guid.NewGuid();
        var state = WizardState(survey.Id, invitationId);

        var result = await CreateService().AdvanceWizardAsync(
            state, 1, back: false, [Ans(q1Id, "yes")], ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.Navigated);
        state.CurrentPage.Should().Be(2);
        state.Started.Should().BeTrue();
        state.Answers.Should().ContainKey(q1Id.ToString());
        await _repo.Received(1).MarkInvitationStartedAsync(invitationId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdvanceWizardAsync_reports_missing_required_and_stays_on_page()
    {
        var survey = SurveyForWizard(out var q1Id, out _);
        var state = WizardState(survey.Id);

        var result = await CreateService().AdvanceWizardAsync(state, 1, back: false, [], ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.ValidationFailed);
        result.MissingRequired.Should().BeEquivalentTo(new[] { q1Id });
        state.CurrentPage.Should().Be(1);
    }

    [HumansFact]
    public async Task AdvanceWizardAsync_submits_after_last_visible_page()
    {
        var survey = SurveyForWizard(out var q1Id, out var q2Id);
        var state = WizardState(survey.Id);
        state.Answers[q1Id.ToString()] = new SurveyWizardAnswer { SelectedOptionValues = ["yes"] };
        state.CurrentPage = 2;
        state.Started = true;
        SurveyResponse? captured = null;
        _repo.When(r => r.AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<SurveyResponse>());

        var result = await CreateService().AdvanceWizardAsync(
            state, 2, back: false, [TextAns(q2Id, "done")], ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.Submitted);
        captured.Should().NotBeNull();
        captured!.Anonymity.Should().Be(ResponseAnonymity.Anonymous);
        captured.Answers.Select(a => a.QuestionId).Should().BeEquivalentTo(new[] { q1Id, q2Id });
    }

    [HumansFact]
    public async Task AdvanceWizardAsync_returns_closed_when_survey_not_open()
    {
        var survey = SurveyForWizard(out var q1Id, out _);
        survey.Status = SurveyStatus.Closed;
        var state = WizardState(survey.Id);

        var result = await CreateService().AdvanceWizardAsync(state, 1, back: false, [Ans(q1Id, "yes")], ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.Closed);
        state.Started.Should().BeFalse();
        await _repo.DidNotReceive().MarkInvitationStartedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdvanceWizardAsync_back_navigates_without_validation()
    {
        var survey = SurveyForWizard(out var q1Id, out _);
        var state = WizardState(survey.Id);
        state.Answers[q1Id.ToString()] = new SurveyWizardAnswer { SelectedOptionValues = ["yes"] };
        state.CurrentPage = 2;
        state.Started = true;

        // Back from page 2 with the required q2 (page 2 text) unanswered must not validate.
        var result = await CreateService().AdvanceWizardAsync(state, 2, back: true, [], ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.Navigated);
        state.CurrentPage.Should().Be(1);
    }

    // ── Results aggregation (Task 6.1) ─────────────────────────────────────────

    private static SurveyQuestion ChoiceQuestion(Guid id, Guid surveyId, SurveyQuestionType type, int order, params (string Value, string Label, int Order)[] opts) => new()
    {
        Id = id,
        SurveyId = surveyId,
        PageNumber = 1,
        Order = order,
        Type = type,
        Prompt = L($"Q{order.ToString(System.Globalization.CultureInfo.InvariantCulture)}"),
        Options = opts.Select(o => new SurveyQuestionOption { Id = Guid.NewGuid(), QuestionId = id, Order = o.Order, Value = o.Value, Label = L(o.Label) })
            .ToList(),
    };

    private static SurveyQuestion RatingQuestion(Guid id, Guid surveyId, int order, int min, int max) => new()
    {
        Id = id,
        SurveyId = surveyId,
        PageNumber = 1,
        Order = order,
        Type = SurveyQuestionType.Rating,
        Prompt = L($"Q{order.ToString(System.Globalization.CultureInfo.InvariantCulture)}"),
        RatingMin = min,
        RatingMax = max,
    };

    private static SurveyQuestion TextQuestion(Guid id, Guid surveyId, int order) => new()
    {
        Id = id,
        SurveyId = surveyId,
        PageNumber = 1,
        Order = order,
        Type = SurveyQuestionType.ShortText,
        Prompt = L($"Q{order.ToString(System.Globalization.CultureInfo.InvariantCulture)}"),
    };

    private static SurveyResponse SubmittedResponse(
        Guid surveyId, ResponseAnonymity anonymity, SurveyInputMethod inputMethod, Instant submittedAt,
        Guid? userId, params SurveyAnswer[] answers) => new()
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            UserId = userId,
            Anonymity = anonymity,
            InputMethod = inputMethod,
            SubmittedAt = submittedAt,
            Answers = answers.ToList(),
        };

    private static SurveyAnswer ChoiceAnswer(Guid questionId, params string[] values) =>
        new() { Id = Guid.NewGuid(), QuestionId = questionId, SelectedOptionValues = values.ToList() };

    private static SurveyAnswer RatingAnswer(Guid questionId, int value) =>
        new() { Id = Guid.NewGuid(), QuestionId = questionId, RatingValue = value };

    private static SurveyAnswer TextAnswer(Guid questionId, string? text) =>
        new() { Id = Guid.NewGuid(), QuestionId = questionId, TextValue = text };

    [HumansFact]
    public async Task GetResultsAsync_returns_null_when_survey_missing()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Survey?)null);

        var result = await CreateService().GetResultsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetResultsAsync_aggregates_choice_rating_and_freetext_over_submitted_responses()
    {
        var surveyId = Guid.NewGuid();
        var choiceId = Guid.NewGuid();
        var ratingId = Guid.NewGuid();
        var textId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = new List<SurveyQuestion>
        {
            ChoiceQuestion(choiceId, surveyId, SurveyQuestionType.SingleChoice, 1,
                ("yes", "Yes", 1), ("no", "No", 2), ("maybe", "Maybe", 3)),
            RatingQuestion(ratingId, surveyId, 2, 1, 5),
            TextQuestion(textId, surveyId, 3),
        };
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var now = _clock.GetCurrentInstant();
        var responses = new List<SurveyResponse>
        {
            // Identified, link
            SubmittedResponse(surveyId, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink, now, Guid.NewGuid(),
                ChoiceAnswer(choiceId, "yes"), RatingAnswer(ratingId, 5), TextAnswer(textId, "great")),
            // CompletionTracked, link
            SubmittedResponse(surveyId, ResponseAnonymity.CompletionTracked, SurveyInputMethod.UserSpecificLink, now, null,
                ChoiceAnswer(choiceId, "yes"), RatingAnswer(ratingId, 3), TextAnswer(textId, "ok")),
            // Anonymous, slug — empty/null text dropped
            SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null,
                ChoiceAnswer(choiceId, "no"), RatingAnswer(ratingId, 1), TextAnswer(textId, "")),
        };
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(responses);
        _repo.GetInvitedCountsBySurveyAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [surveyId] = 4 });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var result = await CreateService().GetResultsAsync(surveyId, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.ResponseCount.Should().Be(3);
        result.InvitedCount.Should().Be(4);
        result.ResponseRate.Should().BeApproximately(3d / 4d, 0.0001);

        var choice = result.Questions.Single(q => q.QuestionId == choiceId);
        choice.OptionCounts.Should().HaveCount(3);
        choice.OptionCounts.Select(o => o.Value).Should().ContainInOrder("yes", "no", "maybe");
        choice.OptionCounts.Single(o => string.Equals(o.Value, "yes", StringComparison.Ordinal)).Count.Should().Be(2);
        choice.OptionCounts.Single(o => string.Equals(o.Value, "no", StringComparison.Ordinal)).Count.Should().Be(1);
        var maybe = choice.OptionCounts.Single(o => string.Equals(o.Value, "maybe", StringComparison.Ordinal));
        maybe.Count.Should().Be(0);
        maybe.Percent.Should().Be(0);
        choice.OptionCounts.Single(o => string.Equals(o.Value, "yes", StringComparison.Ordinal)).Percent
            .Should().BeApproximately(200d / 3d, 0.0001);

        var rating = result.Questions.Single(q => q.QuestionId == ratingId);
        rating.RatingAverage.Should().BeApproximately((5 + 3 + 1) / 3d, 0.0001);
        rating.RatingDistribution.Select(b => b.Value).Should().ContainInOrder(1, 2, 3, 4, 5);
        rating.RatingDistribution.Single(b => b.Value == 1).Count.Should().Be(1);
        rating.RatingDistribution.Single(b => b.Value == 2).Count.Should().Be(0);
        rating.RatingDistribution.Single(b => b.Value == 5).Count.Should().Be(1);

        var text = result.Questions.Single(q => q.QuestionId == textId);
        text.FreeTextAnswers.Should().BeEquivalentTo(new[] { "great", "ok" });
    }

    [HumansFact]
    public async Task GetResultsAsync_percent_base_is_respondents_who_answered_the_question()
    {
        // Branched/optional questions aren't seen by everyone: the percent base must be the
        // respondents who answered THIS question, not all submissions.
        var surveyId = Guid.NewGuid();
        var choiceId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = new List<SurveyQuestion>
        {
            ChoiceQuestion(choiceId, surveyId, SurveyQuestionType.SingleChoice, 1, ("yes", "Yes", 1), ("no", "No", 2)),
        };
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var now = _clock.GetCurrentInstant();
        var responses = new List<SurveyResponse>
        {
            SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null,
                ChoiceAnswer(choiceId, "yes")),
            SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null,
                ChoiceAnswer(choiceId, "no")),
            // Never saw the question (hidden by branching) — no answer row for it.
            SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null),
        };
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(responses);
        _repo.GetInvitedCountsBySurveyAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var result = await CreateService().GetResultsAsync(surveyId, TestContext.Current.CancellationToken);

        var choice = result!.Questions.Single(q => q.QuestionId == choiceId);
        // 2 answered → "yes" is 1 of 2 = 50%, not 1 of 3 ≈ 33%.
        choice.OptionCounts.Single(o => string.Equals(o.Value, "yes", StringComparison.Ordinal)).Percent
            .Should().BeApproximately(50d, 0.0001);
    }

    [HumansFact]
    public async Task GetResultsAsync_only_identified_responses_appear_in_drilldown_but_all_count_in_aggregates()
    {
        var surveyId = Guid.NewGuid();
        var choiceId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = new List<SurveyQuestion>
        {
            ChoiceQuestion(choiceId, surveyId, SurveyQuestionType.SingleChoice, 1, ("yes", "Yes", 1), ("no", "No", 2)),
        };
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var now = _clock.GetCurrentInstant();
        var identifiedUser = Guid.NewGuid();
        var responses = new List<SurveyResponse>
        {
            SubmittedResponse(surveyId, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink, now, identifiedUser,
                ChoiceAnswer(choiceId, "yes")),
            SubmittedResponse(surveyId, ResponseAnonymity.CompletionTracked, SurveyInputMethod.UserSpecificLink, now, null,
                ChoiceAnswer(choiceId, "yes")),
            SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null,
                ChoiceAnswer(choiceId, "no")),
        };
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(responses);
        _repo.GetInvitedCountsBySurveyAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [identifiedUser] = UserInfoWithName(identifiedUser, "Sparkle") }));

        var result = await CreateService().GetResultsAsync(surveyId, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.ResponseCount.Should().Be(3);
        // All three counted in the aggregate (2 yes, 1 no).
        var choice = result.Questions.Single(q => q.QuestionId == choiceId);
        choice.OptionCounts.Single(o => string.Equals(o.Value, "yes", StringComparison.Ordinal)).Count.Should().Be(2);
        choice.OptionCounts.Single(o => string.Equals(o.Value, "no", StringComparison.Ordinal)).Count.Should().Be(1);

        // Only the Identified response is in the drill-down, with the stitched name and resolved selected label.
        result.IdentifiedRespondents.Should().ContainSingle();
        var detail = result.IdentifiedRespondents[0];
        detail.UserId.Should().Be(identifiedUser);
        detail.Name.Should().Be("Sparkle");
        detail.Answers.Should().ContainSingle();
        detail.Answers[0].QuestionId.Should().Be(choiceId);
        detail.Answers[0].SelectedLabels.Should().ContainInOrder("Yes");
    }

    [HumansFact]
    public async Task GetResultsAsync_identified_respondent_name_falls_back_to_user_id_when_unresolved()
    {
        var surveyId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = new List<SurveyQuestion>();
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var user = Guid.NewGuid();
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new List<SurveyResponse>
            {
                SubmittedResponse(surveyId, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
                    _clock.GetCurrentInstant(), user),
            });
        _repo.GetInvitedCountsBySurveyAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var result = await CreateService().GetResultsAsync(surveyId, TestContext.Current.CancellationToken);

        result!.IdentifiedRespondents.Should().ContainSingle();
        result.IdentifiedRespondents[0].Name.Should().Be(user.ToString());
    }

    [HumansFact]
    public async Task GetResultsAsync_builds_funnel_from_started_count_public_count_and_input_method_splits()
    {
        var surveyId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = new List<SurveyQuestion>();
        survey.PublicStartedCount = 9;
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var now = _clock.GetCurrentInstant();
        var responses = new List<SurveyResponse>
        {
            SubmittedResponse(surveyId, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink, now, Guid.NewGuid()),
            SubmittedResponse(surveyId, ResponseAnonymity.CompletionTracked, SurveyInputMethod.UserSpecificLink, now, null),
            SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null),
            SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null),
        };
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(responses);
        _repo.GetStartedInvitationCountAsync(surveyId, Arg.Any<CancellationToken>()).Returns(7);
        _repo.GetInvitedCountsBySurveyAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [surveyId] = 10 });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var result = await CreateService().GetResultsAsync(surveyId, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Funnel.LinkStarted.Should().Be(7);
        result.Funnel.LinkFinished.Should().Be(2);   // Identified + CompletionTracked via link
        result.Funnel.SlugStarted.Should().Be(9);
        result.Funnel.SlugFinished.Should().Be(2);   // two anonymous slug responses
    }

    // ── Raw per-response export (Task 6.2) ─────────────────────────────────────

    [HumansFact]
    public async Task GetResponseExportAsync_returns_null_when_survey_missing()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Survey?)null);

        var export = await CreateService().GetResponseExportAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        export.Should().BeNull();
    }

    [HumansFact]
    public async Task GetResponseExportAsync_populates_identity_only_for_identified_rows()
    {
        var surveyId = Guid.NewGuid();
        var choiceId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = new List<SurveyQuestion>
        {
            ChoiceQuestion(choiceId, surveyId, SurveyQuestionType.SingleChoice, 1, ("yes", "Yes", 1), ("no", "No", 2)),
        };
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var now = _clock.GetCurrentInstant();
        var identifiedUser = Guid.NewGuid();
        var responses = new List<SurveyResponse>
        {
            SubmittedResponse(surveyId, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink, now, identifiedUser,
                ChoiceAnswer(choiceId, "yes")),
            SubmittedResponse(surveyId, ResponseAnonymity.CompletionTracked, SurveyInputMethod.UserSpecificLink, now, null,
                ChoiceAnswer(choiceId, "yes")),
            SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null,
                ChoiceAnswer(choiceId, "no")),
        };
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(responses);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [identifiedUser] = UserInfoWithName(identifiedUser, "Sparkle") }));

        var export = await CreateService().GetResponseExportAsync(surveyId, TestContext.Current.CancellationToken);

        export.Should().NotBeNull();
        export.Rows.Should().HaveCount(3);   // every tier appears so totals reconcile

        var identified = export.Rows.Single(r => r.Anonymity == ResponseAnonymity.Identified);
        identified.UserId.Should().Be(identifiedUser);
        identified.UserName.Should().Be("Sparkle");

        export.Rows.Where(r => r.Anonymity != ResponseAnonymity.Identified)
            .Should().OnlyContain(r => r.UserId == null && r.UserName == null);
    }

    [HumansFact]
    public async Task GetResponseExportAsync_maps_answer_values_and_labels_and_orders_questions_by_page_then_order()
    {
        var surveyId = Guid.NewGuid();
        var multiId = Guid.NewGuid();
        var textId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        // Declared out of order — export must re-order by (page, order). text is page 2, choice page 1.
        var multi = ChoiceQuestion(multiId, surveyId, SurveyQuestionType.MultiChoice, 1, ("a", "Apple", 1), ("b", "Banana", 2));
        var text = TextQuestion(textId, surveyId, 2);
        text.PageNumber = 2;
        survey.Questions = new List<SurveyQuestion> { text, multi };
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var now = _clock.GetCurrentInstant();
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new List<SurveyResponse>
            {
                SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null,
                    ChoiceAnswer(multiId, "a", "b"), TextAnswer(textId, "free")),
            });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var export = await CreateService().GetResponseExportAsync(surveyId, TestContext.Current.CancellationToken);

        export.Should().NotBeNull();
        export.Questions.Select(q => q.QuestionId).Should().ContainInOrder(multiId, textId);

        var row = export.Rows.Single();
        var multiAnswer = row.Answers.Single(a => a.QuestionId == multiId);
        multiAnswer.SelectedValues.Should().ContainInOrder("a", "b");
        multiAnswer.SelectedLabels.Should().ContainInOrder("Apple", "Banana");

        var textAnswer = row.Answers.Single(a => a.QuestionId == textId);
        textAnswer.TextValue.Should().Be("free");
    }

    [HumansFact]
    public async Task GetResponseExportAsync_orders_rows_by_submitted_at()
    {
        var surveyId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = new List<SurveyQuestion>();
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var t0 = _clock.GetCurrentInstant();
        var early = SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, t0, null);
        var late = SubmittedResponse(surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, t0 + Duration.FromHours(2), null);
        // Provide them out of chronological order.
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new List<SurveyResponse> { late, early });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var export = await CreateService().GetResponseExportAsync(surveyId, TestContext.Current.CancellationToken);

        export!.Rows.Select(r => r.ResponseId).Should().ContainInOrder(early.Id, late.Id);
    }

    // ── GDPR export contributor (Task 7.1) ─────────────────────────────────────

    [HumansFact]
    public async Task ContributeForUserAsync_returns_survey_responses_slice_with_title_and_answers()
    {
        var userId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var choiceId = Guid.NewGuid();
        var textId = Guid.NewGuid();
        var ratingId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Title = L("Summer Feedback");
        survey.Questions = new List<SurveyQuestion>
        {
            ChoiceQuestion(choiceId, surveyId, SurveyQuestionType.SingleChoice, 1, ("yes", "Yes", 1), ("no", "No", 2)),
            RatingQuestion(ratingId, surveyId, 2, 1, 5),
            TextQuestion(textId, surveyId, 3),
        };
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var response = SubmittedResponse(surveyId, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
            _clock.GetCurrentInstant(), userId,
            ChoiceAnswer(choiceId, "yes"), RatingAnswer(ratingId, 4), TextAnswer(textId, "loved it"));
        _repo.GetIdentifiedResponsesForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<SurveyResponse> { response });

        var slices = await CreateService().ContributeForUserAsync(userId, TestContext.Current.CancellationToken);

        var slice = slices.Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(GdprExportSections.SurveyResponses);
        slice.Data.Should().NotBeNull();

        // The payload serialises the user's response (title + answers). Round-trip through JSON to assert shape.
        var json = System.Text.Json.JsonSerializer.Serialize(slice.Data);
        json.Should().Contain("Summer Feedback");
        json.Should().Contain("Yes");        // resolved choice label
        json.Should().Contain("loved it");   // free-text value
        json.Should().Contain("4");          // rating value
    }

    [HumansFact]
    public async Task ContributeForUserAsync_returns_empty_collection_slice_when_user_has_no_identified_responses()
    {
        var userId = Guid.NewGuid();
        _repo.GetIdentifiedResponsesForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<SurveyResponse>());

        var slices = await CreateService().ContributeForUserAsync(userId, TestContext.Current.CancellationToken);

        var slice = slices.Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(GdprExportSections.SurveyResponses);
        // Collection sections emit [] (not null) when the user has no records.
        var json = System.Text.Json.JsonSerializer.Serialize(slice.Data);
        json.Should().Be("[]");
    }

    [HumansFact]
    public async Task ContributeForUserAsync_surfaces_only_what_the_repo_returns()
    {
        // The repository query is the gate that excludes CompletionTracked/Anonymous tiers.
        // The contributor must not re-add any other responses — it surfaces exactly the repo result.
        var userId = Guid.NewGuid();
        var surveyId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = new List<SurveyQuestion>();
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var one = SubmittedResponse(surveyId, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
            _clock.GetCurrentInstant(), userId);
        _repo.GetIdentifiedResponsesForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<SurveyResponse> { one });

        var slices = await CreateService().ContributeForUserAsync(userId, TestContext.Current.CancellationToken);

        // Exactly one slice, and it carries exactly the single response the repo surfaced.
        slices.Should().ContainSingle();
        await _repo.Received(1).GetIdentifiedResponsesForUserAsync(userId, Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().GetResponsesForResultsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
