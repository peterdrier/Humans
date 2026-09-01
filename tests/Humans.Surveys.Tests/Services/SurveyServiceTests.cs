using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using Humans.Surveys.Data;
using Humans.Shifts.Contracts;
using Humans.Surveys.Services;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Surveys.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using Humans.Surveys.Contracts;

namespace Humans.Surveys.Tests.Services;

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
    private readonly IFileStorage _fileStorage = Substitute.For<IFileStorage>();

    public SurveyServiceTests()
    {
        _repo.GetInvitationsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SurveyInvitation>());
    }

    private SurveyService CreateService(ILogger<SurveyService>? logger = null) => new(
        _repo, _audit, _clock, logger ?? NullLogger<SurveyService>.Instance,
        _teamService, _userService, _ticketService, _shiftView,
        _userEmailService, _emailService, _emailMessages, _tokenProvider, _translation, _fileStorage);

    private static LocalizedText L(string en) => new(new Dictionary<string, string>(StringComparer.Ordinal) { ["en"] = en });

    private static OptionInput Opt(string value, string label, int order) => new(null, order, value, L(label));

    private static QuestionInput Q(string prompt, SurveyQuestionType type, int page, int order, params OptionInput[] opts) =>
        new(null, page, order, type, L(prompt), LocalizedText.Empty, false, null, null, LocalizedText.Empty, LocalizedText.Empty, null, opts.ToList());

    private static QuestionInput GridInput(
        Guid? id = null,
        int page = 1,
        int order = 1,
        GridSelectionMode? mode = GridSelectionMode.Single,
        IReadOnlyList<OptionInput>? columns = null,
        IReadOnlyList<GridRowInput>? rows = null) =>
        new(
            id, page, order, SurveyQuestionType.Grid, L("Availability"), LocalizedText.Empty,
            false, null, null, LocalizedText.Empty, LocalizedText.Empty, null,
            columns ?? [Opt("morning", "Morning", 1), Opt("afternoon", "Afternoon", 2)],
            mode,
            rows ?? [new GridRowInput("monday", L("Monday")), new GridRowInput("tuesday", L("Tuesday"))]);

    private static QuestionInput RankedInput(
        Guid? id = null,
        bool allowEqualRanks = true,
        bool allowReject = true,
        IReadOnlyList<OptionInput>? options = null) =>
        new(
            id, 1, 1, SurveyQuestionType.RankedChoice, L("Rank dates"), LocalizedText.Empty,
            true, null, null, LocalizedText.Empty, LocalizedText.Empty, null,
            options ?? [Opt("a", "A", 1), Opt("b", "B", 2), Opt("c", "C", 3)],
            RankedSettings: new RankedQuestionSettings(
                allowEqualRanks,
                allowReject,
                RankedVotingMethod.RankedPairs));

    private static SurveyEditInput Input(params QuestionInput[] qs) =>
        new(
            L("Title"), L("Intro"), L("Thanks"),
            LocalizedText.Empty, LocalizedText.Empty,
            "en", false, null, null, null, null, null, null, qs.ToList());

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

    [HumansFact]
    public async Task CreateAsync_persists_trimmed_invitation_email_copy()
    {
        Survey? captured = null;
        _repo.When(r => r.AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<Survey>());
        var input = Input() with
        {
            InvitationEmailSubject = L("  Help choose our dates  "),
            InvitationEmailMessage = L("  Tell us what works.  "),
        };

        await CreateService().CreateAsync(
            input, Guid.NewGuid(), TestContext.Current.CancellationToken);

        captured!.InvitationEmailSubject.Resolve("en", "en").Should().Be("Help choose our dates");
        captured.InvitationEmailMessage.Resolve("en", "en").Should().Be("Tell us what works.");
    }

    [HumansFact]
    public async Task CreateAsync_rejects_multiline_or_overlong_invitation_subjects()
    {
        var multiline = Input() with { InvitationEmailSubject = L("First\nSecond") };
        var overlong = Input() with { InvitationEmailSubject = L(new string('x', 201)) };

        var multilineAct = async () => await CreateService().CreateAsync(
            multiline, Guid.NewGuid(), TestContext.Current.CancellationToken);
        var overlongAct = async () => await CreateService().CreateAsync(
            overlong, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await multilineAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*single line*");
        await overlongAct.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*200 characters or fewer*");
        await _repo.DidNotReceive().AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateAsync_rejects_overlong_invitation_messages()
    {
        var input = Input() with
        {
            InvitationEmailMessage = L(new string('x', 4001)),
        };

        var act = async () => await CreateService().CreateAsync(
            input, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*4000 characters or fewer*");
        await _repo.DidNotReceive().AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateAsync_persists_grid_configuration()
    {
        Survey? captured = null;
        _repo.When(r => r.AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<Survey>());

        await CreateService().CreateAsync(
            Input(GridInput(mode: GridSelectionMode.Multiple)),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        var grid = captured!.Questions.Should().ContainSingle().Subject;
        grid.Type.Should().Be(SurveyQuestionType.Grid);
        grid.GridSelectionMode.Should().Be(GridSelectionMode.Multiple);
        grid.GridRows!.Select(row => row.Value).Should().ContainInOrder("monday", "tuesday");
        grid.Options.Select(column => column.Value).Should().ContainInOrder("morning", "afternoon");
    }

    [HumansFact]
    public async Task CreateAsync_saves_and_persists_an_information_image()
    {
        Survey? captured = null;
        _repo.When(r => r.AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<Survey>());
        var questionId = Guid.NewGuid();
        await using var content = new MemoryStream([1, 2, 3]);
        var information = new QuestionInput(
            questionId, 1, 0, SurveyQuestionType.Information,
            L("Conditions"), L("Use this context before choosing."), true, null, null,
            LocalizedText.Empty, LocalizedText.Empty, null, [],
            InformationImages:
            [
                new InformationImageInput(
                    null,
                    L("Fire risk"),
                    L("Fire risk forecast table"),
                    Upload: new SurveyImageUpload(content, "image/png", "fire-risk.png", 3)),
            ]);

        await CreateService().CreateAsync(
            Input(information), Guid.NewGuid(), TestContext.Current.CancellationToken);

        var saved = captured!.Questions.Should().ContainSingle().Subject;
        saved.Type.Should().Be(SurveyQuestionType.Information);
        saved.IsRequired.Should().BeFalse();
        saved.Options.Should().BeEmpty();
        var image = saved.InformationImages.Should().ContainSingle().Subject;
        image.StoragePath.Should().StartWith($"uploads/surveys/{captured.Id}/{questionId}/");
        image.StoragePath.Should().EndWith(".png");
        image.Label.Resolve("en", "en").Should().Be("Fire risk");
        await _fileStorage.Received(1).SaveAsync(
            image.StoragePath, content, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateAsync_does_not_delete_a_removed_image_that_a_concurrent_editor_may_restore()
    {
        var survey = SurveyWith(SurveyStatus.Draft, null, null);
        var questionId = Guid.NewGuid();
        survey.Questions =
        [
            new SurveyQuestion
            {
                Id = questionId,
                SurveyId = survey.Id,
                PageNumber = 1,
                Type = SurveyQuestionType.Information,
                HelpText = L("Context"),
                InformationImages =
                [
                    new SurveyInformationImage(
                        Guid.NewGuid(),
                        "uploads/surveys/old.png",
                        "image/png",
                        "old.png",
                        L("Old"),
                        L("Old data")),
                ],
            },
        ];
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        var updated = new QuestionInput(
            questionId, 1, 0, SurveyQuestionType.Information,
            LocalizedText.Empty, L("Updated context"), false, null, null,
            LocalizedText.Empty, LocalizedText.Empty, null, [],
            InformationImages: []);

        await CreateService().UpdateAsync(
            survey.Id, Input(updated), Guid.NewGuid(), TestContext.Current.CancellationToken);

        await _repo.Received(1).UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
        await _fileStorage.DidNotReceive().DeleteAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateAsync_rejects_an_image_row_without_an_upload()
    {
        var information = new QuestionInput(
            Guid.NewGuid(), 1, 0, SurveyQuestionType.Information,
            L("Conditions"), L("Context"), false, null, null,
            LocalizedText.Empty, LocalizedText.Empty, null, [],
            InformationImages:
            [
                new InformationImageInput(
                    null,
                    L("Fire risk"),
                    L("Fire risk forecast table")),
            ]);

        var act = async () => await CreateService().CreateAsync(
            Input(information), Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*select the file again*");
        await _repo.DidNotReceive().AddAsync(
            Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateAsync_rejects_a_grid_with_more_than_five_columns()
    {
        var columns = Enumerable.Range(1, 6)
            .Select(index => Opt($"c{index}", $"Column {index}", index))
            .ToList();

        var act = async () => await CreateService().CreateAsync(
            Input(GridInput(columns: columns)),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*between one and five columns*");
        await _repo.DidNotReceive().AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateAsync_rejects_a_grid_without_rows()
    {
        var act = async () => await CreateService().CreateAsync(
            Input(GridInput(rows: [])),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*at least one row*");
    }

    [HumansFact]
    public async Task CreateAsync_rejects_a_grid_without_a_selection_mode()
    {
        var act = async () => await CreateService().CreateAsync(
            Input(GridInput(mode: null)),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must choose a selection mode*");
    }

    [HumansFact]
    public async Task CreateAsync_rejects_a_grid_with_an_undefined_selection_mode()
    {
        var act = async () => await CreateService().CreateAsync(
            Input(GridInput(mode: (GridSelectionMode)2)),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must choose a selection mode*");
    }

    [HumansFact]
    public async Task CreateAsync_rejects_a_grid_with_a_blank_row_value()
    {
        var act = async () => await CreateService().CreateAsync(
            Input(GridInput(rows: [new GridRowInput(" ", L("Blank"))])),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*row values must not be blank*");
    }

    [HumansFact]
    public async Task CreateAsync_rejects_a_grid_with_duplicate_row_values()
    {
        var act = async () => await CreateService().CreateAsync(
            Input(GridInput(rows:
            [
                new GridRowInput("monday", L("Monday")),
                new GridRowInput("monday", L("Monday again")),
            ])),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*row values must be unique*");
    }

    [HumansFact]
    public async Task CreateAsync_rejects_a_grid_with_duplicate_column_values()
    {
        var act = async () => await CreateService().CreateAsync(
            Input(GridInput(columns:
            [
                Opt("morning", "Morning", 1),
                Opt("morning", "Morning again", 2),
            ])),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*column values must be unique*");
    }

    [HumansFact]
    public async Task CreateAsync_rejects_using_a_grid_as_a_branching_source()
    {
        var gridId = Guid.NewGuid();
        var dependentId = Guid.NewGuid();
        var grid = GridInput(gridId);
        var dependent = new QuestionInput(
            dependentId, 2, 1, SurveyQuestionType.ShortText,
            L("Why?"), LocalizedText.Empty, false, null, null,
            LocalizedText.Empty, LocalizedText.Empty,
            new BranchCondition
            {
                Clauses =
                {
                    new BranchClause
                    {
                        QuestionId = gridId,
                        Operator = BranchOperator.Is,
                        OptionValues = { "morning" },
                    },
                },
            },
            []);

        var act = async () => await CreateService().CreateAsync(
            Input(grid, dependent),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be branching sources*");
        await _repo.DidNotReceive().AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    private static SurveyEditInput InputWithSlug(string? slug) =>
        new(
            L("Title"), L("Intro"), L("Thanks"),
            LocalizedText.Empty, LocalizedText.Empty,
            "en", true, null, null, null, null, null, slug, []);

    private static SurveyEditInput InputWithAudience(
        SurveyAudienceType audience, Guid? teamId = null, Instant? loggedInSince = null) =>
        new(
            L("Title"), L("Intro"), L("Thanks"),
            LocalizedText.Empty, LocalizedText.Empty,
            "en", false, null, null,
            audience, teamId, loggedInSince, null, []);

    /// <summary>Entity matching <see cref="Input"/> field-for-field, so an unchanged update diffs empty.</summary>
    private static Survey ExistingSurveyMatchingInput(Guid id) => new()
    {
        Id = id,
        Title = L("Title"),
        Intro = L("Intro"),
        ThankYou = L("Thanks"),
        InvitationEmailSubject = LocalizedText.Empty,
        InvitationEmailMessage = LocalizedText.Empty,
        DefaultCulture = "en",
        AllowAnonymous = false,
        Status = SurveyStatus.Draft,
        Questions = [],
    };

    [HumansFact]
    public async Task UpdateAsync_audit_names_the_changed_fields()
    {
        var id = Guid.NewGuid();
        var actor = Guid.NewGuid();
        _repo.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Survey?>(ExistingSurveyMatchingInput(id)));
        var input = new SurveyEditInput(
            L("Title"), L("Intro"), L("Thanks"),
            LocalizedText.Empty, LocalizedText.Empty,
            "en", true, null, null, null, null, null, "town-hall",
            [Q("How was it?", SurveyQuestionType.ShortText, page: 1, order: 1)]);

        await CreateService().UpdateAsync(id, input, actor);

        await _audit.Received(1).LogAsync(
            AuditAction.SurveyUpdated, "Survey", id,
            Arg.Is<string>(d =>
                d.Contains("anonymous responses enabled") &&
                d.Contains("public slug set") &&
                d.Contains("1 question(s) added")),
            actor);
    }

    [HumansFact]
    public async Task UpdateAsync_audit_counts_a_rating_label_only_edit()
    {
        var id = Guid.NewGuid();
        var qid = Guid.NewGuid();
        var existing = ExistingSurveyMatchingInput(id);
        existing.Questions =
        [
            new SurveyQuestion
            {
                Id = qid,
                SurveyId = id,
                PageNumber = 1,
                Order = 1,
                Type = SurveyQuestionType.Rating,
                Prompt = L("Rate it"),
                RatingMin = 1,
                RatingMax = 5,
                RatingMinLabel = L("Bad"),
                RatingMaxLabel = L("Great"),
            },
        ];
        _repo.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Survey?>(existing));
        var input = Input(new QuestionInput(
            qid, 1, 1, SurveyQuestionType.Rating,
            L("Rate it"), LocalizedText.Empty, false, 1, 5,
            L("Poor"), L("Great"), null, []));

        await CreateService().UpdateAsync(id, input, Guid.NewGuid());

        await _audit.Received(1).LogAsync(
            AuditAction.SurveyUpdated, "Survey", id,
            Arg.Is<string>(d => d.Contains("1 question(s) edited")), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task UpdateAsync_audit_counts_a_grid_selection_mode_only_edit()
    {
        var id = Guid.NewGuid();
        var qid = Guid.NewGuid();
        var existing = ExistingSurveyMatchingInput(id);
        existing.Questions =
        [
            new SurveyQuestion
            {
                Id = qid,
                SurveyId = id,
                PageNumber = 1,
                Order = 1,
                Type = SurveyQuestionType.Grid,
                Prompt = L("Availability"),
                GridSelectionMode = GridSelectionMode.Single,
                GridRows = [new SurveyGridRow("monday", L("Monday"))],
                Options = [new SurveyQuestionOption { Id = Guid.NewGuid(), Order = 1, Value = "morning", Label = L("Morning") }],
            },
        ];
        _repo.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Survey?>(existing));
        var input = Input(GridInput(
            qid,
            mode: GridSelectionMode.Multiple,
            columns: [Opt("morning", "Morning", 1)],
            rows: [new GridRowInput("monday", L("Monday"))]));

        await CreateService().UpdateAsync(id, input, Guid.NewGuid());

        await _audit.Received(1).LogAsync(
            AuditAction.SurveyUpdated, "Survey", id,
            Arg.Is<string>(d => d.Contains("1 question(s) edited")), Arg.Any<Guid>());
    }

    [HumansFact]
    public async Task UpdateAsync_audit_stays_bare_when_nothing_changed()
    {
        var id = Guid.NewGuid();
        _repo.GetByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Survey?>(ExistingSurveyMatchingInput(id)));

        await CreateService().UpdateAsync(id, Input(), Guid.NewGuid());

        await _audit.Received(1).LogAsync(
            AuditAction.SurveyUpdated, "Survey", id, "Updated survey", Arg.Any<Guid>());
    }

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
    public async Task CreateAsync_rejects_team_audience_without_team_and_does_not_persist()
    {
        var act = async () => await CreateService().CreateAsync(
            InputWithAudience(SurveyAudienceType.Team), Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*team is required*");
        await _repo.DidNotReceive().AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateAsync_rejects_logged_in_since_audience_without_cutoff_and_does_not_persist()
    {
        var act = async () => await CreateService().UpdateAsync(
            Guid.NewGuid(), InputWithAudience(SurveyAudienceType.LoggedInSince),
            Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cutoff date is required*");
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CreateAsync_rejects_unknown_audience_and_does_not_persist()
    {
        var act = async () => await CreateService().CreateAsync(
            InputWithAudience((SurveyAudienceType)999), Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not supported*");
        await _repo.DidNotReceive().AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
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
        var input = new SurveyEditInput(
            L("T"), L("I"), L("Ty"),
            LocalizedText.Empty, LocalizedText.Empty,
            "en", false, null, null, null, null, null, null,
            new List<QuestionInput> { q1, q2 });

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
    public async Task PreFillTranslationsAsync_includes_grid_row_labels()
    {
        var survey = SurveyWith(SurveyStatus.Draft, null, null);
        var questionId = Guid.NewGuid();
        survey.Questions =
        [
            new SurveyQuestion
            {
                Id = questionId,
                SurveyId = survey.Id,
                PageNumber = 1,
                Order = 1,
                Type = SurveyQuestionType.Grid,
                Prompt = L("Availability"),
                GridSelectionMode = GridSelectionMode.Single,
                GridRows = [new SurveyGridRow("monday", L("Monday"))],
                Options =
                [
                    new SurveyQuestionOption
                    {
                        Id = Guid.NewGuid(),
                        QuestionId = questionId,
                        Order = 1,
                        Value = "morning",
                        Label = L("Morning"),
                    },
                ],
            },
        ];
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        Survey? captured = null;
        _repo.When(r => r.UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>()))
            .Do(ci => captured = ci.Arg<Survey>());
        StubTranslationAsMarker();

        var filled = await CreateService().PreFillTranslationsAsync(
            survey.Id, ["en", "es"], Guid.NewGuid(), TestContext.Current.CancellationToken);

        filled.Should().Be(4); // survey title + question prompt + column label + row label
        captured!.Questions.Single().GridRows!.Single().Label.Values["es"].Should().Be("es:Monday");
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

    [HumansFact]
    public async Task PreFillTranslationsAsync_includes_invitation_subject_and_message()
    {
        var survey = SurveyWith(SurveyStatus.Draft, null, null);
        survey.InvitationEmailSubject = L("Choose a date");
        survey.InvitationEmailMessage = L("Tell us what works.");
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        Survey? captured = null;
        _repo.When(r => r.UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>()))
             .Do(ci => captured = ci.Arg<Survey>());
        StubTranslationAsMarker();

        var filled = await CreateService().PreFillTranslationsAsync(
            survey.Id, ["en", "fr"], Guid.NewGuid(), TestContext.Current.CancellationToken);

        filled.Should().Be(3); // title + invitation subject + invitation message
        captured!.InvitationEmailSubject.Values["fr"].Should().Be("fr:Choose a date");
        captured.InvitationEmailMessage.Values["fr"].Should().Be("fr:Tell us what works.");
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

    private static UserInfo UserWithLastLogin(Guid id, Instant? lastLogin, UserState state = UserState.Active) =>
        UserInfo.Create(
            new User { Id = id, PreferredLanguage = "en", LastLoginAt = lastLogin, State = state },
            [], [], [], null, []);

    private static UserInfo Asociado(Guid id, MembershipTier tier = MembershipTier.Asociado)
    {
        var profile = UserFixtures.Profile(
            burnerName: "Voter",
            firstName: "Eligible",
            lastName: "Human",
            isApproved: true,
            membershipTier: tier);
        return new UserInfo(
            Id: id,
            BurnerName: profile.BurnerName,
            IsGdprAnonymized: false,
            PreferredLanguage: "en",
            FallbackPictureUrl: null,
            CreatedAt: Instant.MinValue,
            LastLoginAt: null,
            LastConsentReminderSentAt: null,
            DeletionRequestedAt: null,
            DeletionScheduledFor: null,
            DeletionEligibleAfter: null,
            UnsubscribedFromCampaigns: false,
            ICalToken: null,
            SuppressScheduleChangeEmails: false,
            MagicLinkSentAt: null,
            ContactSource: null,
            ExternalSourceId: null,
            MergedToUserId: null,
            MergedAt: null,
            IdentityEmailColumn: null,
            UserEmails: [],
            EventParticipations: [],
            ExternalLogins: [],
            Profile: profile,
            CommunicationPreferences: [])
        {
            State = UserState.Active,
        };
    }

    private static TeamInfo TeamWith(Guid teamId, params Guid[] memberUserIds) => new(
        teamId, "Team", null, "team",
        IsActive: true, IsSystemTeam: false, SystemTeamType.None, RequiresApproval: false,
        IsPublicPage: false, IsHidden: false, IsPromotedToDirectory: false, Instant.MinValue,
        memberUserIds
            .Select(u => new TeamMemberInfo(Guid.NewGuid(), u, "M", null, null, TeamMemberRole.Member, Instant.MinValue))
            .ToList());

    [HumansFact]
    public async Task PreviewAudienceCountAsync_team_counts_only_net_new_members()
    {
        var teamId = Guid.NewGuid();
        Guid alreadyInvited = Guid.NewGuid(), newA = Guid.NewGuid(), newB = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Draft, SurveyAudienceType.Team, teamId);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(TeamWith(teamId, alreadyInvited, newA, newB));
        _repo.GetInvitedUserIdsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid> { alreadyInvited });

        var count = await CreateService().PreviewAudienceCountAsync(survey.Id, TestContext.Current.CancellationToken);

        count.Should().Be(2);
    }

    // ── issue #1065: an unhandled audience type resolves to nobody, warned not silent ──

    [HumansFact]
    public async Task PreviewAudienceCountAsync_unknown_audience_type_warns_and_resolves_to_nobody()
    {
        var survey = SurveyWith(SurveyStatus.Draft, (SurveyAudienceType)99, null);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        var logger = new CapturingLogger<SurveyService>();

        var count = await CreateService(logger).PreviewAudienceCountAsync(survey.Id, TestContext.Current.CancellationToken);

        count.Should().Be(0);
        var entry = logger.Entries.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogLevel.Warning);
        entry.Message.Should().Contain(nameof(SurveyAudienceType)).And.Contain("99");
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
        _repo.GetInvitedUserIdsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

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
        _repo.GetInvitedUserIdsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());

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
    public async Task SendInvitesAsync_upgrades_an_unsent_public_participation_instead_of_inserting_a_duplicate()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, teamId);
        var participation = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = survey.Id,
            UserId = userId,
            SentAt = null,
        };
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(TeamWith(teamId, userId));
        _repo.GetInvitedUserIdsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        _repo.GetInvitationsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns([participation]);
        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "human@example.org" });
        _userService.GetUserInfosAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));

        var result = await CreateService().SendInvitesAsync(
            survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.InvitationsCreated.Should().Be(1);
        await _repo.Received(1).UpdateInvitationStatusAsync(
            participation.Id, EmailOutboxStatus.Queued, _clock.GetCurrentInstant(),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddInvitationAndSaveAsync(
            Arg.Any<SurveyInvitation>(), Arg.Any<CancellationToken>());
        _tokenProvider.Received(1).Create(participation.Id);
        await _emailService.Received(1).SendAsync(
            Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendInvitesAsync_skips_a_completed_public_participant()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, teamId);
        var participation = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = survey.Id,
            UserId = userId,
            SentAt = null,
            Completed = true,
        };
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(TeamWith(teamId, userId));
        _repo.GetInvitedUserIdsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        _repo.GetInvitationsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns([participation]);

        var result = await CreateService().SendInvitesAsync(
            survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.InvitationsCreated.Should().Be(0);
        result.EmailsQueued.Should().Be(0);
        await _repo.DidNotReceive().UpdateInvitationStatusAsync(
            Arg.Any<Guid>(), Arg.Any<EmailOutboxStatus>(), Arg.Any<Instant>(),
            Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendAsync(
            Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendInvitesAsync_resolves_title_and_custom_copy_in_each_recipient_culture()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, teamId);
        survey.Title = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = "Availability",
            ["fr"] = "Disponibilités",
        });
        survey.InvitationEmailSubject = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = "Choose a date",
            ["fr"] = "Choisissez une date",
        });
        survey.InvitationEmailMessage = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = "Tell us what works.",
            ["fr"] = "Dites-nous ce qui vous convient.",
        });
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(TeamWith(teamId, userId));
        _repo.GetInvitedUserIdsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "human@example.org" });
        _userService.GetUserInfosAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>
                {
                    [userId] = UserInfoWithName(userId, "Étincelle", "fr"),
                }));

        await CreateService().SendInvitesAsync(
            survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        _emailMessages.Received(1).SurveyInvitation(
            "human@example.org",
            "Étincelle",
            "Disponibilités",
            Arg.Any<string>(),
            "fr",
            "Choisissez une date",
            "Dites-nous ce qui vous convient.");
    }

    [HumansFact]
    public async Task SendInvitesAsync_leaves_optional_copy_blank_when_only_an_unrelated_culture_exists()
    {
        var teamId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, teamId);
        survey.InvitationEmailSubject = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = string.Empty,
            ["es"] = "Elige una fecha",
        });
        survey.InvitationEmailMessage = new LocalizedText(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["en"] = string.Empty,
            ["es"] = "Dinos qué te conviene.",
        });
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _teamService.GetTeamAsync(teamId, Arg.Any<CancellationToken>())
            .Returns(TeamWith(teamId, userId));
        _repo.GetInvitedUserIdsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns(new HashSet<Guid>());
        _userEmailService.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string> { [userId] = "human@example.org" });
        _userService.GetUserInfosAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>
                {
                    [userId] = UserInfoWithName(userId, "Étincelle", "fr"),
                }));

        await CreateService().SendInvitesAsync(
            survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        _emailMessages.Received(1).SurveyInvitation(
            "human@example.org",
            "Étincelle",
            Arg.Any<string>(),
            Arg.Any<string>(),
            "fr",
            string.Empty,
            string.Empty);
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

    [HumansFact]
    public async Task SendInvitesAsync_throws_when_team_audience_has_no_team()
    {
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, null);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);

        var act = async () => await CreateService().SendInvitesAsync(
            survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*team is required*");
        await _repo.DidNotReceive().AddInvitationAndSaveAsync(
            Arg.Any<SurveyInvitation>(), Arg.Any<CancellationToken>());
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
    public async Task SendDueRemindersAsync_skips_an_Open_survey_that_is_past_its_ClosesAt()
    {
        var now = _clock.GetCurrentInstant();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, Guid.NewGuid());
        survey.ClosesAt = now - Duration.FromDays(1);
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

        // The link would land on the Closed page, and the one-shot ReminderSentAt would be spent.
        count.Should().Be(0);
        await _emailService.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().SetReminderSentAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SendDueRemindersAsync_skips_an_Open_survey_that_has_not_reached_its_OpensAt()
    {
        var now = _clock.GetCurrentInstant();
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Team, Guid.NewGuid());
        survey.OpensAt = now + Duration.FromDays(1);
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

        count.Should().Be(0);
        await _repo.DidNotReceive().SetReminderSentAsync(Arg.Any<Guid>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
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

    [HumansFact]
    public async Task GetOfficialLinkAsync_uses_the_current_humans_unspent_invitation()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        survey.PublicSlug = "public-fallback";
        var userId = Guid.NewGuid();
        var invitation = InvitationFor(survey.Id, userId);
        invitation.SentAt = _clock.GetCurrentInstant();
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _repo.GetInvitationsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns([invitation]);
        _tokenProvider.Create(invitation.Id).Returns("official-token");

        var link = await CreateService().GetOfficialLinkAsync(
            survey.Id, userId, TestContext.Current.CancellationToken);

        link.Should().Be(new SurveyOfficialLink("official-token", null));
    }

    [HumansFact]
    public async Task GetOfficialLinkAsync_falls_back_to_the_public_slug()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        survey.PublicSlug = "public-survey";
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);

        var link = await CreateService().GetOfficialLinkAsync(
            survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        link.Should().Be(new SurveyOfficialLink(null, "public-survey"));
    }

    private static UserInfo UserInfoWithName(Guid id, string burnerName, string culture = "en") => new(
        id, burnerName, false, culture, null, Instant.MinValue, null, null, null, null, null,
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
        var ranked = new RankedAnswer([["a"], ["b"]], ["c"]);
        var draft = new SurveyResponse
        {
            Id = Guid.NewGuid(),
            SurveyId = survey.Id,
            UserId = userId,
            InvitationId = invitation.Id,
            Anonymity = ResponseAnonymity.Identified,
            Answers = new List<SurveyAnswer>
            {
                new()
                {
                    Id = Guid.NewGuid(),
                    QuestionId = questionId,
                    SelectedOptionValues = ["yes"],
                    TextValue = "note",
                    RatingValue = 4,
                    RankedValue = ranked,
                },
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
        answer.RankedValue.Should().Be(ranked);
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
    public async Task ResolveAnswerContextAsync_rejects_a_non_asociado_from_an_asociado_vote()
    {
        var survey = SurveyWith(SurveyStatus.Open, SurveyAudienceType.Asociados, null);
        survey.IsAsociadoVote = true;
        var userId = Guid.NewGuid();
        var invitation = InvitationFor(survey.Id, userId);
        _tokenProvider.Resolve("vote").Returns(invitation.Id);
        _repo.GetInvitationByIdAsync(invitation.Id, Arg.Any<CancellationToken>()).Returns(invitation);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Asociado(userId, MembershipTier.Colaborador));

        var ctx = await CreateService().ResolveAnswerContextAsync(
            "vote", TestContext.Current.CancellationToken);

        ctx.Should().NotBeNull();
        ctx.IsEligible.Should().BeFalse();
        ctx.HasResumableDraft.Should().BeFalse();
        await _repo.DidNotReceive().GetDraftResponseAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
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

        var id = await CreateService().StartIdentifiedDraftAsync(
            surveyId, Guid.NewGuid(), userId, SurveyInputMethod.UserSpecificLink,
            "en", TestContext.Current.CancellationToken);

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

        var id = await CreateService().StartIdentifiedDraftAsync(
            surveyId, invitationId, userId, SurveyInputMethod.UserSpecificLink,
            "es", TestContext.Current.CancellationToken);

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

    [HumansFact]
    public async Task StartPublicTrackedResponseAsync_identified_creates_unsent_participation_and_slug_draft()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participation = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            UserId = userId,
        };
        SurveyResponse? capturedDraft = null;
        _repo.GetOrCreateParticipationAsync(
                surveyId, userId, _clock.GetCurrentInstant(), Arg.Any<CancellationToken>())
            .Returns(participation);
        _repo.GetDraftResponseAsync(surveyId, userId, Arg.Any<CancellationToken>())
            .Returns((SurveyResponse?)null);
        _repo.When(r => r.AddResponseAsync(
                Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()))
            .Do(call => capturedDraft = call.Arg<SurveyResponse>());

        var result = await CreateService().StartPublicTrackedResponseAsync(
            surveyId, userId, ResponseAnonymity.Identified, "en",
            TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.ParticipationId.Should().Be(participation.Id);
        result.DraftResponseId.Should().Be(capturedDraft!.Id);
        result.DraftAnswers.Should().BeEmpty();
        capturedDraft.InvitationId.Should().Be(participation.Id);
        capturedDraft.UserId.Should().Be(userId);
        capturedDraft.Anonymity.Should().Be(ResponseAnonymity.Identified);
        capturedDraft.InputMethod.Should().Be(SurveyInputMethod.Slug);
    }

    [HumansFact]
    public async Task StartPublicTrackedResponseAsync_completion_tracked_creates_ledger_without_draft()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participation = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            UserId = userId,
        };
        _repo.GetOrCreateParticipationAsync(
                surveyId,
                userId,
                Instant.FromUtc(1970, 1, 1, 0, 0),
                Arg.Any<CancellationToken>())
            .Returns(participation);

        var result = await CreateService().StartPublicTrackedResponseAsync(
            surveyId, userId, ResponseAnonymity.CompletionTracked, "en",
            TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.ParticipationId.Should().Be(participation.Id);
        result.DraftResponseId.Should().BeNull();
        result.DraftAnswers.Should().BeEmpty();
        await _repo.DidNotReceive().AddResponseAsync(
            Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task StartPublicTrackedResponseAsync_completed_participation_refuses_another_tracked_start()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var participation = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            UserId = userId,
            Completed = true,
        };
        _repo.GetOrCreateParticipationAsync(
                surveyId, userId, _clock.GetCurrentInstant(), Arg.Any<CancellationToken>())
            .Returns(participation);

        var result = await CreateService().StartPublicTrackedResponseAsync(
            surveyId, userId, ResponseAnonymity.Identified, "en",
            TestContext.Current.CancellationToken);

        result.Should().BeNull();
        await _repo.DidNotReceive().AddResponseAsync(
            Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task StartPublicTrackedResponseAsync_identified_restores_existing_draft_answers()
    {
        var surveyId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var participation = new SurveyInvitation
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            UserId = userId,
        };
        var draft = new SurveyResponse
        {
            Id = Guid.NewGuid(),
            SurveyId = surveyId,
            InvitationId = participation.Id,
            UserId = userId,
            Anonymity = ResponseAnonymity.Identified,
            InputMethod = SurveyInputMethod.UserSpecificLink,
            Answers =
            [
                new SurveyAnswer
                {
                    Id = Guid.NewGuid(),
                    QuestionId = questionId,
                    SelectedOptionValues = ["yes"],
                    TextValue = "saved",
                },
            ],
        };
        _repo.GetOrCreateParticipationAsync(
                surveyId, userId, _clock.GetCurrentInstant(), Arg.Any<CancellationToken>())
            .Returns(participation);
        _repo.GetDraftResponseAsync(surveyId, userId, Arg.Any<CancellationToken>())
            .Returns(draft);

        var result = await CreateService().StartPublicTrackedResponseAsync(
            surveyId, userId, ResponseAnonymity.Identified, "en",
            TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result!.DraftResponseId.Should().Be(draft.Id);
        result.DraftAnswers.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new SurveyDraftAnswer(
                questionId, ["yes"], "saved", null));
        await _repo.DidNotReceive().AddResponseAsync(
            Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
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

        await _repo.Received(1).FinalizeIdentifiedResponseAsync(
            invitationId,
            draftId,
            Arg.Is<IReadOnlyList<SurveyAnswer>>(a => a.Count == 2),
            _clock.GetCurrentInstant(),
            SurveyInputMethod.UserSpecificLink,
            "en",
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitResponseAsync_rechecks_asociado_eligibility_before_finalising()
    {
        var survey = SurveyForSubmit(out var questionId, out _);
        survey.IsAsociadoVote = true;
        var userId = Guid.NewGuid();
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Asociado(userId, MembershipTier.Colaborador));
        var submission = new SurveySubmission(
            survey.Id, Guid.NewGuid(), userId, Guid.NewGuid(),
            ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink, "en",
            [Ans(questionId, "yes")]);

        var act = async () => await CreateService().SubmitResponseAsync(
            submission, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*active, approved Asociado*");
        await _repo.DidNotReceive().FinalizeIdentifiedResponseAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<SurveyAnswer>>(),
            Arg.Any<Instant>(),
            Arg.Any<SurveyInputMethod>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitResponseAsync_completion_tracked_stores_unlinked_response_and_completes_invitation()
    {
        var survey = SurveyForSubmit(out var q1Id, out _);
        var invitationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        SurveyResponse? captured = null;
        _repo.When(r => r.FinalizeCompletionTrackedResponseAsync(
                invitationId, userId, Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()))
            .Do(ci => captured = ci.Arg<SurveyResponse>());
        var submission = new SurveySubmission(
            survey.Id, invitationId, userId, null,
            ResponseAnonymity.CompletionTracked, SurveyInputMethod.UserSpecificLink, "en",
            new List<SurveyAnswerInput> { Ans(q1Id, "yes") });

        await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.UserId.Should().BeNull();
        captured.InvitationId.Should().BeNull();
        captured.Anonymity.Should().Be(ResponseAnonymity.CompletionTracked);
        captured.SubmittedAt.Should().Be(_clock.GetCurrentInstant());
        await _repo.Received(1).FinalizeCompletionTrackedResponseAsync(
            invitationId, userId, captured, Arg.Any<CancellationToken>());
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
        await _repo.DidNotReceive().FinalizeIdentifiedResponseAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<IReadOnlyList<SurveyAnswer>>(),
            Arg.Any<Instant>(),
            Arg.Any<SurveyInputMethod>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().FinalizeCompletionTrackedResponseAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<SurveyResponse>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SubmitResponseAsync_stores_null_for_an_unanswered_optional_grid()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        var grid = GridQuestion(Guid.NewGuid(), survey.Id);
        survey.Questions = [grid];
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        SurveyResponse? captured = null;
        _repo.When(r => r.AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>()))
            .Do(ci => captured = ci.Arg<SurveyResponse>());
        var submission = new SurveySubmission(
            survey.Id, null, null, null,
            ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en",
            [new SurveyAnswerInput(
                grid.Id, [], null, null,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal))]);

        await CreateService().SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        captured!.Answers.Should().ContainSingle().Which.GridSelections.Should().BeNull();
    }

    [HumansFact]
    public async Task SubmitResponseAsync_rejects_an_incomplete_required_grid()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        var grid = GridQuestion(Guid.NewGuid(), survey.Id, GridSelectionMode.Single);
        grid.IsRequired = true;
        survey.Questions = [grid];
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        var submission = new SurveySubmission(
            survey.Id, null, null, null,
            ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, "en",
            [new SurveyAnswerInput(
                grid.Id, [], null, null,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
                {
                    ["monday"] = ["morning"],
                })]);

        var act = async () => await CreateService()
            .SubmitResponseAsync(submission, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Required survey questions*");
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(
            Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
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

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("This invitation has already submitted a response.");
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().FinalizeCompletionTrackedResponseAsync(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<SurveyResponse>(),
            Arg.Any<CancellationToken>());
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
    public async Task AdvanceWizardAsync_reports_invalid_ranked_answer_and_preserves_it()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        var questionId = Guid.NewGuid();
        var question = RankedQuestion(questionId, survey.Id);
        question.RankedSettings = RankedQuestionSettings.Default with { AllowEqualRanks = false };
        survey.Questions = [question];
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        var state = WizardState(survey.Id);
        var ranked = new RankedAnswer([["a", "b"]], []);
        var posted = new SurveyAnswerInput(questionId, [], null, null, null, ranked);

        var result = await CreateService().AdvanceWizardAsync(
            state,
            page: 1,
            back: false,
            [posted],
            ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.ValidationFailed);
        result.MissingRequired.Should().BeEmpty();
        result.InvalidAnswers.Should().ContainSingle().Which.Should().Be(questionId);
        state.CurrentPage.Should().Be(1);
        state.Answers[questionId.ToString()].RankedValue.Should().BeEquivalentTo(ranked);
        state.Started.Should().BeFalse();
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(
            Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
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
    public async Task AdvanceWizardAsync_submits_completion_tracked_response_and_completes_invitation()
    {
        var survey = SurveyForWizard(out var q1Id, out var q2Id);
        var invitationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var state = WizardState(survey.Id, invitationId);
        state.Anonymity = ResponseAnonymity.CompletionTracked;
        state.UserId = userId;
        state.Answers[q1Id.ToString()] = new SurveyWizardAnswer { SelectedOptionValues = ["yes"] };
        state.CurrentPage = 2;
        state.Started = true;

        var result = await CreateService().AdvanceWizardAsync(
            state, 2, back: false, [TextAns(q2Id, "done")], ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.Submitted);
        await _repo.Received(1).FinalizeCompletionTrackedResponseAsync(
            invitationId, userId, Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdvanceWizardAsync_returns_submitted_when_invitation_already_completed()
    {
        var survey = SurveyForWizard(out var q1Id, out var q2Id);
        var invitation = InvitationFor(survey.Id, Guid.NewGuid());
        invitation.Completed = true;
        var state = WizardState(survey.Id, invitation.Id);
        state.Anonymity = ResponseAnonymity.CompletionTracked;
        state.UserId = invitation.UserId;
        state.Answers[q1Id.ToString()] = new SurveyWizardAnswer { SelectedOptionValues = ["yes"] };
        state.CurrentPage = 2;
        state.Started = true;
        _repo.GetInvitationByIdAsync(invitation.Id, Arg.Any<CancellationToken>()).Returns(invitation);

        var result = await CreateService().AdvanceWizardAsync(
            state, 2, back: false, [TextAns(q2Id, "done")], ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.Submitted);
        await _repo.DidNotReceive().FinalizeCompletionTrackedResponseAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
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

    [HumansFact]
    public async Task AdvanceWizardAsync_normalizes_grid_rows_columns_and_single_selection()
    {
        var gridId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        survey.Questions =
        [
            new SurveyQuestion
            {
                Id = gridId,
                SurveyId = survey.Id,
                PageNumber = 1,
                Order = 1,
                Type = SurveyQuestionType.Grid,
                Prompt = L("Availability"),
                GridSelectionMode = GridSelectionMode.Single,
                GridRows =
                [
                    new SurveyGridRow("monday", L("Monday")),
                    new SurveyGridRow("tuesday", L("Tuesday")),
                ],
                Options =
                [
                    new SurveyQuestionOption
                    {
                        Id = Guid.NewGuid(), QuestionId = gridId, Order = 1,
                        Value = "morning", Label = L("Morning"),
                    },
                    new SurveyQuestionOption
                    {
                        Id = Guid.NewGuid(), QuestionId = gridId, Order = 2,
                        Value = "afternoon", Label = L("Afternoon"),
                    },
                ],
            },
        ];
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        var state = WizardState(survey.Id);
        var posted = new SurveyAnswerInput(
            gridId, [], null, null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["monday"] = ["morning", "afternoon", "morning"],
                ["tuesday"] = ["invalid", "afternoon"],
                ["unknown-row"] = ["morning"],
            });

        await CreateService().AdvanceWizardAsync(
            state, 1, back: true, [posted], ct: TestContext.Current.CancellationToken);

        var captured = state.Answers[gridId.ToString()].GridSelections;
        captured.Keys.Should().BeEquivalentTo("monday", "tuesday");
        captured["monday"].Should().ContainSingle().Which.Should().Be("morning");
        captured["tuesday"].Should().ContainSingle().Which.Should().Be("afternoon");
    }

    [HumansFact]
    public async Task AdvanceWizardAsync_revalidates_an_earlier_required_grid_after_its_schema_changes()
    {
        var gridId = Guid.NewGuid();
        var finalId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        var grid = GridQuestion(gridId, survey.Id, GridSelectionMode.Single);
        grid.PageNumber = 1;
        grid.IsRequired = true;
        survey.Questions =
        [
            grid,
            new SurveyQuestion
            {
                Id = finalId,
                SurveyId = survey.Id,
                PageNumber = 2,
                Order = 1,
                Type = SurveyQuestionType.ShortText,
                Prompt = L("Final"),
            },
        ];
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        var state = WizardState(survey.Id);
        state.CurrentPage = 2;
        state.Started = true;
        state.Answers[gridId.ToString()] = new SurveyWizardAnswer
        {
            GridSelections = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["monday"] = ["morning"],
                ["removed-row"] = ["morning"],
            },
        };

        var result = await CreateService().AdvanceWizardAsync(
            state,
            page: 2,
            back: false,
            [TextAns(finalId, "Done")],
            ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.ValidationFailed);
        result.MissingRequired.Should().ContainSingle().Which.Should().Be(gridId);
        state.CurrentPage.Should().Be(1);
        state.Answers[gridId.ToString()].GridSelections.Keys.Should().ContainSingle().Which.Should().Be("monday");
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(
            Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdvanceWizardAsync_revalidates_the_definition_reloaded_for_submission()
    {
        var gridId = Guid.NewGuid();
        var initial = SurveyWith(SurveyStatus.Open, null, null);
        var initialGrid = GridQuestion(gridId, initial.Id, GridSelectionMode.Single);
        initialGrid.IsRequired = true;
        initialGrid.GridRows = [new SurveyGridRow("monday", L("Monday"))];
        initial.Questions = [initialGrid];

        var reloaded = new Survey
        {
            Id = initial.Id,
            Title = initial.Title,
            DefaultCulture = initial.DefaultCulture,
            Status = SurveyStatus.Open,
        };
        var reloadedGrid = GridQuestion(gridId, reloaded.Id, GridSelectionMode.Single);
        reloadedGrid.IsRequired = true;
        reloaded.Questions = [reloadedGrid];
        _repo.GetByIdAsync(initial.Id, Arg.Any<CancellationToken>())
            .Returns(initial, reloaded);

        var state = WizardState(initial.Id);
        state.Started = true;
        var posted = new SurveyAnswerInput(
            gridId, [], null, null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["monday"] = ["morning"],
            });

        var result = await CreateService().AdvanceWizardAsync(
            state,
            page: 1,
            back: false,
            [posted],
            ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.ValidationFailed);
        result.MissingRequired.Should().ContainSingle().Which.Should().Be(gridId);
        state.CurrentPage.Should().Be(1);
        state.Answers[gridId.ToString()].GridSelections.Keys
            .Should().ContainSingle().Which.Should().Be("monday");
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(
            Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdvanceWizardAsync_reports_ranked_answer_invalidated_at_submission_reload()
    {
        var questionId = Guid.NewGuid();
        var initial = SurveyWith(SurveyStatus.Open, null, null);
        var initialRanked = RankedQuestion(questionId, initial.Id);
        initialRanked.RankedSettings = RankedQuestionSettings.Default with { AllowEqualRanks = true };
        initial.Questions = [initialRanked];

        var reloaded = SurveyWith(SurveyStatus.Open, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(reloaded, initial.Id);
        var reloadedRanked = RankedQuestion(questionId, reloaded.Id);
        reloadedRanked.RankedSettings = RankedQuestionSettings.Default with { AllowEqualRanks = false };
        reloaded.Questions = [reloadedRanked];
        _repo.GetByIdAsync(initial.Id, Arg.Any<CancellationToken>())
            .Returns(initial, reloaded);

        var state = WizardState(initial.Id);
        var ranked = new RankedAnswer([["a", "b"]], []);
        var posted = new SurveyAnswerInput(questionId, [], null, null, null, ranked);

        var result = await CreateService().AdvanceWizardAsync(
            state,
            page: 1,
            back: false,
            [posted],
            ct: TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(SurveyWizardOutcome.ValidationFailed);
        result.MissingRequired.Should().BeEmpty();
        result.InvalidAnswers.Should().ContainSingle().Which.Should().Be(questionId);
        state.CurrentPage.Should().Be(1);
        state.Answers[questionId.ToString()].RankedValue.Should().BeEquivalentTo(ranked);
        await _repo.DidNotReceive().AddResponseWithAnswersAndSaveAsync(
            Arg.Any<SurveyResponse>(), Arg.Any<CancellationToken>());
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

    private static SurveyQuestion RankedQuestion(Guid id, Guid surveyId) => new()
    {
        Id = id,
        SurveyId = surveyId,
        PageNumber = 1,
        Order = 1,
        Type = SurveyQuestionType.RankedChoice,
        Prompt = L("Rank dates"),
        IsRequired = true,
        RankedSettings = RankedQuestionSettings.Default with { AllowReject = true },
        Options =
        [
            new SurveyQuestionOption
            {
                Id = Guid.NewGuid(), QuestionId = id, Order = 1, Value = "a", Label = L("A"),
            },
            new SurveyQuestionOption
            {
                Id = Guid.NewGuid(), QuestionId = id, Order = 2, Value = "b", Label = L("B"),
            },
            new SurveyQuestionOption
            {
                Id = Guid.NewGuid(), QuestionId = id, Order = 3, Value = "c", Label = L("C"),
            },
        ],
    };

    private static SurveyAnswer RankedAnswerFor(
        Guid questionId,
        IReadOnlyList<IReadOnlyList<string>> groups,
        params string[] rejected) =>
        new()
        {
            Id = Guid.NewGuid(),
            QuestionId = questionId,
            RankedValue = new RankedAnswer(groups, rejected),
        };

    private static SurveyQuestion GridQuestion(Guid id, Guid surveyId, GridSelectionMode mode = GridSelectionMode.Multiple) => new()
    {
        Id = id,
        SurveyId = surveyId,
        PageNumber = 1,
        Order = 1,
        Type = SurveyQuestionType.Grid,
        Prompt = L("Availability"),
        GridSelectionMode = mode,
        GridRows =
        [
            new SurveyGridRow("monday", L("Monday")),
            new SurveyGridRow("tuesday", L("Tuesday")),
        ],
        Options =
        [
            new SurveyQuestionOption
            {
                Id = Guid.NewGuid(), QuestionId = id, Order = 1,
                Value = "morning", Label = L("Morning"),
            },
            new SurveyQuestionOption
            {
                Id = Guid.NewGuid(), QuestionId = id, Order = 2,
                Value = "afternoon", Label = L("Afternoon"),
            },
        ],
    };

    private static SurveyAnswer GridAnswer(
        Guid questionId,
        params (string Row, string[] Columns)[] selections) =>
        new()
        {
            Id = Guid.NewGuid(),
            QuestionId = questionId,
            GridSelections = selections.ToDictionary(
                selection => selection.Row,
                selection => selection.Columns.ToList(),
                StringComparer.Ordinal),
        };

    private SurveyInvitation SentInvitation(Guid surveyId, bool completed, bool sent = true) => new()
    {
        Id = Guid.NewGuid(),
        SurveyId = surveyId,
        UserId = Guid.NewGuid(),
        CreatedAt = _clock.GetCurrentInstant(),
        SentAt = sent ? _clock.GetCurrentInstant() : null,
        Completed = completed,
    };

    [HumansFact]
    public async Task GetResultsAsync_returns_null_when_survey_missing()
    {
        _repo.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Survey?)null);

        var result = await CreateService().GetResultsAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task Information_items_are_omitted_from_results_and_response_exports()
    {
        var surveyId = Guid.NewGuid();
        var informationId = Guid.NewGuid();
        var answerId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions =
        [
            new SurveyQuestion
            {
                Id = informationId,
                SurveyId = surveyId,
                PageNumber = 1,
                Order = 0,
                Type = SurveyQuestionType.Information,
                Prompt = L("Forecast"),
                HelpText = L("Context"),
            },
            TextQuestion(answerId, surveyId, 1),
        ];
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<SurveyResponse>());
        _repo.GetInvitedCountsBySurveyAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());

        var results = await CreateService().GetResultsAsync(
            surveyId, TestContext.Current.CancellationToken);
        var export = await CreateService().GetResponseExportAsync(
            surveyId, TestContext.Current.CancellationToken);

        results!.Questions.Should().ContainSingle().Which.QuestionId.Should().Be(answerId);
        export!.Questions.Should().ContainSingle().Which.QuestionId.Should().Be(answerId);
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
        _repo.GetInvitationsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new List<SurveyInvitation>
            {
                SentInvitation(surveyId, completed: true),
                SentInvitation(surveyId, completed: true),
                SentInvitation(surveyId, completed: false),
                SentInvitation(surveyId, completed: false),
                // A logged-in public participant can have a completed ledger row without
                // ever joining the invited pool.
                SentInvitation(surveyId, completed: true, sent: false),
            });
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var result = await CreateService().GetResultsAsync(surveyId, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.ResponseCount.Should().Be(3);
        result.InvitedCount.Should().Be(4);
        // Identified + CompletionTracked invited completions count; the Anonymous
        // response and completed unsent public participation row do not.
        result.ResponseRate.Should().BeApproximately(2d / 4d, 0.0001);

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
    public async Task GetResultsAsync_aggregates_grid_cells_per_row()
    {
        var surveyId = Guid.NewGuid();
        var gridId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = [GridQuestion(gridId, surveyId)];
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);
        var now = _clock.GetCurrentInstant();
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                SubmittedResponse(
                    surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null,
                    GridAnswer(gridId, ("monday", ["morning", "afternoon"]), ("tuesday", ["afternoon"]))),
                SubmittedResponse(
                    surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug, now, null,
                    GridAnswer(gridId, ("monday", ["morning"]))),
            ]);
        _repo.GetInvitedCountsBySurveyAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var result = await CreateService().GetResultsAsync(surveyId, TestContext.Current.CancellationToken);

        var grid = result!.Questions.Should().ContainSingle().Subject.Grid!;
        grid.Mode.Should().Be(GridSelectionMode.Multiple);
        var monday = grid.Rows.Single(row => string.Equals(row.Value, "monday", StringComparison.Ordinal));
        monday.Cells.Single(cell => string.Equals(cell.ColumnValue, "morning", StringComparison.Ordinal)).Count.Should().Be(2);
        monday.Cells.Single(cell => string.Equals(cell.ColumnValue, "morning", StringComparison.Ordinal)).Percent.Should().Be(100);
        monday.Cells.Single(cell => string.Equals(cell.ColumnValue, "afternoon", StringComparison.Ordinal)).Percent.Should().Be(50);
        var tuesday = grid.Rows.Single(row => string.Equals(row.Value, "tuesday", StringComparison.Ordinal));
        tuesday.Cells.Single(cell => string.Equals(cell.ColumnValue, "afternoon", StringComparison.Ordinal)).Percent.Should().Be(100);
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
    public async Task GetScopedResultsAsync_filters_aggregates_without_filtering_participation_or_drilldown()
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

        var service = CreateService();
        var combined = await service.GetScopedResultsAsync(
            surveyId, SurveyResultsScope.Combined, TestContext.Current.CancellationToken);
        var unique = await service.GetScopedResultsAsync(
            surveyId, SurveyResultsScope.Unique, TestContext.Current.CancellationToken);
        var anonymous = await service.GetScopedResultsAsync(
            surveyId, SurveyResultsScope.Anonymous, TestContext.Current.CancellationToken);

        combined.Should().NotBeNull();
        combined.SelectedResponseCount.Should().Be(3);
        combined.Results.ResponseCount.Should().Be(3);
        var combinedChoice = combined.Results.Questions.Single(q => q.QuestionId == choiceId);
        combinedChoice.OptionCounts.Single(o => string.Equals(o.Value, "yes", StringComparison.Ordinal)).Count.Should().Be(2);
        combinedChoice.OptionCounts.Single(o => string.Equals(o.Value, "no", StringComparison.Ordinal)).Count.Should().Be(1);

        unique!.SelectedResponseCount.Should().Be(2);
        var uniqueChoice = unique.Results.Questions.Single(q => q.QuestionId == choiceId);
        uniqueChoice.OptionCounts.Single(o => string.Equals(o.Value, "yes", StringComparison.Ordinal)).Count.Should().Be(2);
        uniqueChoice.OptionCounts.Single(o => string.Equals(o.Value, "no", StringComparison.Ordinal)).Count.Should().Be(0);

        anonymous!.SelectedResponseCount.Should().Be(1);
        var anonymousChoice = anonymous.Results.Questions.Single(q => q.QuestionId == choiceId);
        anonymousChoice.OptionCounts.Single(o => string.Equals(o.Value, "yes", StringComparison.Ordinal)).Count.Should().Be(0);
        anonymousChoice.OptionCounts.Single(o => string.Equals(o.Value, "no", StringComparison.Ordinal)).Count.Should().Be(1);

        // Participation and the Identified drill-down remain combined in every scope.
        anonymous.Results.ResponseCount.Should().Be(3);
        anonymous.Results.IdentifiedRespondents.Should().ContainSingle();
        var detail = anonymous.Results.IdentifiedRespondents[0];
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
    public async Task GetResultsAsync_orders_identified_respondents_by_submission_time()
    {
        var surveyId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = new List<SurveyQuestion>();
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var now = _clock.GetCurrentInstant();
        var earlierUser = Guid.NewGuid();
        var laterUser = Guid.NewGuid();
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(new List<SurveyResponse>
            {
                SubmittedResponse(
                    surveyId, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
                    now, laterUser),
                SubmittedResponse(
                    surveyId, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
                    now - Duration.FromMinutes(5), earlierUser),
            });
        _repo.GetInvitedCountsBySurveyAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>
                {
                    [earlierUser] = UserInfoWithName(earlierUser, "Earlier"),
                    [laterUser] = UserInfoWithName(laterUser, "Later"),
                }));

        var result = await CreateService().GetResultsAsync(surveyId, TestContext.Current.CancellationToken);

        result!.IdentifiedRespondents.Select(respondent => respondent.Name)
            .Should().ContainInOrder("Earlier", "Later");
        result.IdentifiedRespondents.Select(respondent => respondent.SubmittedAt)
            .Should().BeInAscendingOrder();
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
    public async Task GetResponseExportAsync_includes_grid_schema_and_resolved_selections()
    {
        var surveyId = Guid.NewGuid();
        var gridId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = [GridQuestion(gridId, surveyId)];
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                SubmittedResponse(
                    surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug,
                    _clock.GetCurrentInstant(), null,
                    GridAnswer(gridId, ("monday", ["morning", "afternoon"]))),
            ]);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var export = await CreateService().GetResponseExportAsync(surveyId, TestContext.Current.CancellationToken);

        var schema = export!.Questions.Should().ContainSingle().Subject;
        schema.GridSelectionMode.Should().Be(GridSelectionMode.Multiple);
        schema.GridRows!.Select(row => row.Label).Should().ContainInOrder("Monday", "Tuesday");
        var answer = export.Rows.Single().Answers.Single();
        answer.GridSelections!["monday"].Should().ContainInOrder("morning", "afternoon");
        var selection = answer.GridSelectionLabels.Should().ContainSingle().Subject;
        selection.RowValue.Should().Be("monday");
        selection.RowLabel.Should().Be("Monday");
        selection.ColumnValues.Should().ContainInOrder("morning", "afternoon");
        selection.ColumnLabels.Should().ContainInOrder("Morning", "Afternoon");
    }

    [HumansFact]
    public async Task GetResponseExportAsync_includes_ranked_schema_availability_and_raw_ballot()
    {
        var surveyId = Guid.NewGuid();
        var questionId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.IsAsociadoVote = true;
        var question = RankedQuestion(questionId, surveyId);
        question.RankedUnavailableOptionValues = ["c"];
        survey.Questions = [question];
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                SubmittedResponse(
                    surveyId,
                    ResponseAnonymity.Identified,
                    SurveyInputMethod.UserSpecificLink,
                    _clock.GetCurrentInstant(),
                    Guid.NewGuid(),
                    RankedAnswerFor(questionId, [["a", "b"]], "c")),
            ]);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var export = await CreateService().GetResponseExportAsync(surveyId, TestContext.Current.CancellationToken);

        var schema = export!.Questions.Should().ContainSingle().Subject;
        schema.RankedSettings.Should().Be(new SurveyRankedSettings(true, true, "RankedPairs"));
        schema.RankedUnavailableOptionValues.Should().ContainSingle().Which.Should().Be("c");
        var ballot = export.Rows.Single().Answers.Single().RankedBallot!;
        ballot.RankGroups.Should().ContainSingle()
            .Which.Should().ContainInOrder("a", "b");
        ballot.Rejected.Should().ContainSingle().Which.Should().Be("c");
    }

    [HumansFact]
    public async Task GetResponseExportAsync_preserves_raw_grid_keys_removed_from_the_current_definition()
    {
        var surveyId = Guid.NewGuid();
        var gridId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Questions = [GridQuestion(gridId, surveyId)];
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);
        _repo.GetResponsesForResultsAsync(surveyId, Arg.Any<CancellationToken>())
            .Returns(
            [
                SubmittedResponse(
                    surveyId, ResponseAnonymity.Anonymous, SurveyInputMethod.Slug,
                    _clock.GetCurrentInstant(), null,
                    GridAnswer(gridId, ("removed-row", ["removed-column"]))),
            ]);
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(new Dictionary<Guid, UserInfo>()));

        var export = await CreateService().GetResponseExportAsync(surveyId, TestContext.Current.CancellationToken);

        var answer = export!.Rows.Single().Answers.Single();
        answer.GridSelections!["removed-row"].Should().ContainSingle().Which.Should().Be("removed-column");
        var labels = answer.GridSelectionLabels.Should().ContainSingle().Subject;
        labels.RowLabel.Should().Be("removed-row");
        labels.ColumnLabels.Should().ContainSingle().Which.Should().Be("removed-column");
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
        var gridId = Guid.NewGuid();
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        typeof(Survey).GetProperty(nameof(Survey.Id))!.SetValue(survey, surveyId);
        survey.Title = L("Summer Feedback");
        survey.Questions = new List<SurveyQuestion>
        {
            ChoiceQuestion(choiceId, surveyId, SurveyQuestionType.SingleChoice, 1, ("yes", "Yes", 1), ("no", "No", 2)),
            RatingQuestion(ratingId, surveyId, 2, 1, 5),
            TextQuestion(textId, surveyId, 3),
            GridQuestion(gridId, surveyId),
        };
        _repo.GetByIdAsync(surveyId, Arg.Any<CancellationToken>()).Returns(survey);

        var response = SubmittedResponse(surveyId, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
            _clock.GetCurrentInstant(), userId,
            ChoiceAnswer(choiceId, "yes"), RatingAnswer(ratingId, 4), TextAnswer(textId, "loved it"),
            GridAnswer(gridId, ("removed-row", ["removed-column"])));
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
        json.Should().Contain("removed-row");
        json.Should().Contain("removed-column");
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

    [HumansFact]
    public async Task CreateAsync_allows_ranked_choice_in_an_ordinary_survey()
    {
        Survey? captured = null;
        _repo.When(repository => repository.AddAsync(
                Arg.Any<Survey>(),
                Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<Survey>());

        await CreateService().CreateAsync(
            Input(RankedInput()),
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        captured.Should().NotBeNull();
        captured!.IsAsociadoVote.Should().BeFalse();
        captured.Questions.Should().ContainSingle()
            .Which.Type.Should().Be(SurveyQuestionType.RankedChoice);
    }

    [HumansFact]
    public async Task CreateAsync_asociado_vote_requires_identified_restricted_configuration()
    {
        var ranked = RankedInput();
        var service = CreateService();

        var anonymousVote = async () => await service.CreateAsync(
            Input(ranked) with
            {
                IsAsociadoVote = true,
                AudienceType = SurveyAudienceType.Asociados,
                AllowAnonymous = true,
            },
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);
        var publicVote = async () => await service.CreateAsync(
            Input(ranked) with
            {
                IsAsociadoVote = true,
                AudienceType = SurveyAudienceType.Asociados,
                PublicSlug = "vote",
            },
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);
        var wrongAudience = async () => await service.CreateAsync(
            Input(ranked) with
            {
                IsAsociadoVote = true,
                AudienceType = SurveyAudienceType.AllActiveMembers,
            },
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await anonymousVote.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*identified*");
        await publicVote.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*public link*");
        await wrongAudience.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Asociados audience*");
        await _repo.DidNotReceive().AddAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task OpenAsync_rejects_reopening_a_closed_asociado_vote()
    {
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        survey.IsAsociadoVote = true;
        _repo.GetStatusAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(SurveyStatus.Closed);
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);

        var act = async () => await CreateService().OpenAsync(
            survey.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be reopened*");
        await _repo.DidNotReceive().SetStatusAsync(
            Arg.Any<Guid>(), Arg.Any<SurveyStatus>(), Arg.Any<Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateAsync_cannot_edit_an_asociado_vote_after_opening()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        survey.IsAsociadoVote = true;
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);

        var act = async () => await CreateService().UpdateAsync(
            survey.Id,
            Input() with { IsAsociadoVote = false },
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be edited after it has opened*");
        await _repo.DidNotReceive().UpdateAsync(
            Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Open_asociado_vote_exposes_participation_but_embargoes_answer_results_and_exports()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        survey.IsAsociadoVote = true;
        var questionId = Guid.NewGuid();
        survey.Questions = [TextQuestion(questionId, survey.Id, 1)];
        var response = SubmittedResponse(
            survey.Id,
            ResponseAnonymity.Identified,
            SurveyInputMethod.UserSpecificLink,
            _clock.GetCurrentInstant(),
            Guid.NewGuid(),
            TextAnswer(questionId, "secret"));
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _repo.GetResponsesForResultsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns([response]);
        _repo.GetInvitedCountsBySurveyAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [survey.Id] = 4 });
        _repo.GetStartedInvitationCountAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(2);

        var service = CreateService();
        var scoped = await service.GetScopedResultsAsync(
            survey.Id, SurveyResultsScope.Combined, TestContext.Current.CancellationToken);
        var publicResults = await service.GetResultsAsync(survey.Id, TestContext.Current.CancellationToken);
        var export = await service.GetResponseExportAsync(survey.Id, TestContext.Current.CancellationToken);

        scoped!.IsEmbargoed.Should().BeTrue();
        scoped.Results.ResponseCount.Should().Be(1);
        scoped.Results.InvitedCount.Should().Be(4);
        scoped.Results.Questions.Should().BeEmpty();
        scoped.Results.IdentifiedRespondents.Should().BeEmpty();
        scoped.RankedQuestions.Should().BeEmpty();
        publicResults.Should().BeNull();
        export.Should().BeNull();
    }

    [HumansFact]
    public async Task UpdateAsync_freezes_ranked_candidates_order_and_settings_after_first_saved_answer()
    {
        var survey = SurveyWith(SurveyStatus.Open, null, null);
        survey.IsAsociadoVote = true;
        var questionId = Guid.NewGuid();
        survey.Questions = [RankedQuestion(questionId, survey.Id)];
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _repo.HasSavedAnswersAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(true);
        var changed = RankedInput(
            questionId,
            allowEqualRanks: false,
            options: [Opt("b", "B", 1), Opt("a", "A", 2), Opt("c", "C", 3)]);

        var act = async () => await CreateService().UpdateAsync(
            survey.Id,
            Input(changed) with
            {
                IsAsociadoVote = true,
                AudienceType = SurveyAudienceType.Asociados,
            },
            Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot be edited after it has opened*");
        await _repo.DidNotReceive().UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetRankedAvailabilityAsync_is_post_close_only_and_audits_stable_values()
    {
        var survey = SurveyWith(SurveyStatus.Closed, SurveyAudienceType.Asociados, null);
        survey.IsAsociadoVote = true;
        var questionId = Guid.NewGuid();
        var question = RankedQuestion(questionId, survey.Id);
        question.RankedUnavailableOptionValues = ["b"];
        survey.Questions = [question];
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        Survey? captured = null;
        _repo.When(repo => repo.UpdateAsync(Arg.Any<Survey>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<Survey>());
        var actor = Guid.NewGuid();

        await CreateService().SetRankedAvailabilityAsync(
            survey.Id, questionId, ["c", "unknown"], actor, TestContext.Current.CancellationToken);

        captured!.Questions.Single().RankedUnavailableOptionValues.Should().Equal("c");
        await _audit.Received(1).LogAsync(
            AuditAction.SurveyUpdated,
            AuditEntityTypes.Survey,
            survey.Id,
            Arg.Is<string>(value => value.Contains("unavailable (c)", StringComparison.Ordinal)
                && value.Contains("restored (b)", StringComparison.Ordinal)),
            actor);
    }

    [HumansFact]
    public async Task Closed_ranked_results_preserve_original_and_recount_available_options()
    {
        var survey = SurveyWith(SurveyStatus.Closed, null, null);
        survey.IsAsociadoVote = true;
        var questionId = Guid.NewGuid();
        var question = RankedQuestion(questionId, survey.Id);
        question.RankedUnavailableOptionValues = ["b"];
        survey.Questions = [question];
        var responses = new[]
        {
            SubmittedResponse(
                survey.Id, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
                _clock.GetCurrentInstant(), Guid.NewGuid(),
                RankedAnswerFor(questionId, [["a"], ["b"]], "c")),
            SubmittedResponse(
                survey.Id, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
                _clock.GetCurrentInstant(), Guid.NewGuid(),
                RankedAnswerFor(questionId, [["a"], ["b"], ["c"]])),
            SubmittedResponse(
                survey.Id, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
                _clock.GetCurrentInstant(), Guid.NewGuid(),
                RankedAnswerFor(questionId, [["b"], ["c"], ["a"]])),
            SubmittedResponse(
                survey.Id, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
                _clock.GetCurrentInstant(), Guid.NewGuid(),
                RankedAnswerFor(questionId, [["b"], ["c"], ["a"]])),
            SubmittedResponse(
                survey.Id, ResponseAnonymity.Identified, SurveyInputMethod.UserSpecificLink,
                _clock.GetCurrentInstant(), Guid.NewGuid(),
                RankedAnswerFor(questionId, [["c"], ["a"], ["b"]])),
        };
        _repo.GetByIdAsync(survey.Id, Arg.Any<CancellationToken>()).Returns(survey);
        _repo.GetResponsesForResultsAsync(survey.Id, Arg.Any<CancellationToken>())
            .Returns(responses);
        _repo.GetInvitedCountsBySurveyAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int>());
        _userService.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, UserInfo>());

        var scoped = await CreateService().GetScopedResultsAsync(
            survey.Id, SurveyResultsScope.Combined, TestContext.Current.CancellationToken);

        var ranked = scoped!.RankedQuestions![questionId];
        ranked.OriginalOfficialResult.WinnerValue.Should().Be("a");
        ranked.CurrentOfficialResult.WinnerValue.Should().Be("c");
        ranked.OriginalPreferenceCycle.Should().HaveCount(4);
        ranked.OriginalPreferenceCycle[0].Should().Be(ranked.OriginalPreferenceCycle[^1]);
        ranked.CurrentPreferenceCycle.Should().BeEmpty();
        ranked.Methods.Select(method => method.Method).Should().Equal(
            "Ranked Pairs (official)", "Condorcet check", "Borda Count");
        ranked.Candidates.Single(candidate => string.Equals(candidate.Value, "c", StringComparison.Ordinal))
            .RejectionCount.Should().Be(1);
        ranked.Candidates.Single(candidate => string.Equals(candidate.Value, "b", StringComparison.Ordinal))
            .IsAvailable.Should().BeFalse();
    }
}
