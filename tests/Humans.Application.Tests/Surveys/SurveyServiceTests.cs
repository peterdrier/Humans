using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
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

    private SurveyService CreateService() => new(
        _repo, _audit, _clock, NullLogger<SurveyService>.Instance,
        _teamService, _userService, _ticketService, _shiftView);

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
}
