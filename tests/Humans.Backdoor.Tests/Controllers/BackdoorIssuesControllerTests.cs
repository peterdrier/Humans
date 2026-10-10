using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Filters;
using Humans.Base.Constants;
using Humans.Issues.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using System.Security.Claims;
using Xunit;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="BackdoorIssuesController"/> — the machine surface at
/// <c>/api/backdoor/issues</c>. Mocks <see cref="IIssueTriage"/> and drives the controller
/// directly (no HTTP roundtrip). The auth filter is covered by
/// <see cref="BackdoorApiKeyAuthFilterTests"/>.
/// </summary>
public class BackdoorIssuesControllerTests
{
    private static readonly Guid KeyOwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IIssueTriage _issues = Substitute.For<IIssueTriage>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly BackdoorIssuesController _sut;

    [HumansTheory]
    [InlineData(-1, false)]
    [InlineData(999, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    public void Json_triage_models_validate_defined_enum_values(int value, bool valid)
    {
        var status = JsonSerializer.Deserialize<UpdateIssueStatusModel>("{\"Status\":" + value + "}")!;
        var create = JsonSerializer.Deserialize<ApiCreateIssueModel>("{\"Category\":" + value + "}")!;
        var feedback = JsonSerializer.Deserialize<UpdateFeedbackStatusModel>("{\"Status\":" + value + "}")!;
        create.ReporterUserId = KeyOwnerId;
        create.Title = "Title";
        create.Description = "Description";

        Validator.TryValidateObject(status, new ValidationContext(status), [], validateAllProperties: true).Should().Be(valid);
        Validator.TryValidateObject(create, new ValidationContext(create), [], validateAllProperties: true).Should().Be(valid);
        Validator.TryValidateObject(feedback, new ValidationContext(feedback), [], validateAllProperties: true).Should().Be(valid);
    }

    public BackdoorIssuesControllerTests()
    {
        // GetUserInfosAsync never returns null; an unstubbed ValueTask would.
        _users.GetUserInfosAsync(default!, default)
            .ReturnsForAnyArgs(new Dictionary<Guid, UserInfo>());
        _sut = new BackdoorIssuesController(_issues, _users, NullLogger<BackdoorIssuesController>.Instance)
        {
            // What BackdoorApiKeyAuthFilter installs once it has resolved the key.
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, KeyOwnerId.ToString())],
                        BackdoorApiKeyAuthFilter.AuthenticationScheme)),
                },
            },
        };
    }

    [HumansFact]
    public async Task AbandonedReadRequests_CancelIssueLoads()
    {
        var id = Guid.NewGuid();
        using var request = new CancellationTokenSource();
        _sut.HttpContext.RequestAborted = request.Token;
        _issues.GetIssueListAsync(Arg.Any<IssueListFilter>(), Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<IssueListSnapshot>>([]);
            });
        _issues.GetIssueByIdAsync(id, Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult<IssueDetail?>(MakeDetail(id, KeyOwnerId));
            });
        _issues.GetThreadAsync(id, Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<IssueThreadEvent>>([]);
            });
        Func<Task<IActionResult>>[] reads =
        [() => _sut.List(null, null, null, null), () => _sut.Get(id), () => _sut.GetComments(id)];
        foreach (var read in reads)
            (await read()).Should().BeOfType<OkObjectResult>();
        await request.CancelAsync();
        foreach (var read in reads)
        {
            Func<Task> operation = async () => await read();
            await operation.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    private static IssueListSnapshot MakeSnapshot(Guid? id = null) => new(
        Id: id ?? Guid.NewGuid(),
        Status: IssueStatus.Open,
        Category: IssueCategory.Bug,
        Section: "Tickets",
        Title: "Issue title",
        Description: "Issue description",
        PageUrl: null,
        UserAgent: null,
        AdditionalContext: null,
        ReporterUserId: Guid.NewGuid(),
        ReporterDisplayName: "Reporter",
        ReporterEmail: "reporter@example.com",
        ReporterPreferredLanguage: "en",
        CreatedAt: Instant.FromUtc(2026, 4, 29, 12, 0),
        UpdatedAt: Instant.FromUtc(2026, 4, 29, 12, 0),
        ResolvedAt: null,
        DueDate: null,
        ScreenshotStoragePath: null,
        CommentCount: 0,
        AssigneeUserId: null,
        AssigneeDisplayName: null,
        GitHubIssueNumber: null);

    private static IssueDetail MakeDetail(Guid id, Guid reporterId) => new(
        Id: id,
        Status: IssueStatus.Open,
        Category: IssueCategory.Bug,
        Section: "Tickets",
        Title: "Issue title",
        Description: "Issue description",
        PageUrl: null,
        UserAgent: null,
        AdditionalContext: null,
        ScreenshotStoragePath: null,
        ReporterUserId: reporterId,
        AssigneeUserId: null,
        ResolvedByUserId: null,
        GitHubIssueNumber: null,
        DueDate: null,
        CreatedAt: Instant.FromUtc(2026, 4, 29, 12, 0),
        UpdatedAt: Instant.FromUtc(2026, 4, 29, 12, 0),
        ResolvedAt: null,
        CommentCount: 0);

    private void StubList(Action<IssueListFilter>? capture = null) =>
        _issues
            .GetIssueListAsync(
                capture is null ? Arg.Any<IssueListFilter>() : Arg.Do<IssueListFilter>(capture),
                Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IssueListSnapshot>>([]));

    // ==========================================================================
    // List
    // ==========================================================================

    [HumansFact]
    public async Task List_returns_all_issues()
    {
        IReadOnlyList<IssueListSnapshot> issues = [MakeSnapshot(), MakeSnapshot(), MakeSnapshot()];
        _issues
            .GetIssueListAsync(Arg.Any<IssueListFilter>(), Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(issues));

        var result = await _sut.List(status: null, category: null, section: null, assignee: null);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value.Should().BeAssignableTo<IEnumerable<object>>().Which.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task List_filters_by_status()
    {
        IssueListFilter? captured = null;
        StubList(f => captured = f);

        await _sut.List(status: IssueStatus.Open, category: null, section: null, assignee: null);

        captured.Should().NotBeNull();
        captured!.Statuses.Should().BeEquivalentTo([IssueStatus.Open]);
    }

    [HumansFact]
    public async Task List_filters_by_section()
    {
        IssueListFilter? captured = null;
        StubList(f => captured = f);

        await _sut.List(status: null, category: null, section: "Tickets", assignee: null);

        captured.Should().NotBeNull();
        captured!.Sections.Should().BeEquivalentTo("Tickets");
    }

    [HumansFact]
    public async Task List_filters_by_reporter()
    {
        IssueListFilter? captured = null;
        StubList(f => captured = f);
        var reporterId = Guid.NewGuid();

        await _sut.List(status: null, category: null, section: null, assignee: null, reporter: reporterId);

        captured.Should().NotBeNull();
        captured!.ReporterUserId.Should().Be(reporterId);
    }

    [HumansFact]
    public async Task List_filters_by_search_text()
    {
        IssueListFilter? captured = null;
        StubList(f => captured = f);

        await _sut.List(status: null, category: null, section: null, assignee: null, search: "duplicate");

        captured.Should().NotBeNull();
        captured!.SearchText.Should().Be("duplicate");
    }

    [HumansFact]
    public async Task List_treats_blank_search_as_unset()
    {
        IssueListFilter? captured = null;
        StubList(f => captured = f);

        await _sut.List(status: null, category: null, section: null, assignee: null, search: "   ");

        captured.Should().NotBeNull();
        captured!.SearchText.Should().BeNull();
    }

    // ==========================================================================
    // Get
    // ==========================================================================

    [HumansFact]
    public async Task Get_returns_NotFound_for_missing_issue()
    {
        var id = Guid.NewGuid();
        _issues.GetIssueByIdAsync(id, Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IssueDetail?>(null));

        var result = await _sut.Get(id);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Get_includes_thread_with_comments_and_audit_events()
    {
        var id = Guid.NewGuid();
        var reporterId = Guid.NewGuid();
        _issues.GetIssueByIdAsync(id, Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IssueDetail?>(MakeDetail(id, reporterId)));

        IReadOnlyList<IssueThreadEvent> thread =
        [
            new IssueCommentEvent(
                CommentId: Guid.NewGuid(),
                At: Instant.FromUtc(2026, 4, 29, 12, 5),
                ActorUserId: reporterId,
                ActorDisplayName: "Reporter",
                ActorIsReporter: true,
                Content: "Still broken"),
            new IssueAuditEvent(
                At: Instant.FromUtc(2026, 4, 29, 12, 10),
                ActorUserId: Guid.NewGuid(),
                ActorDisplayName: "Admin",
                Action: AuditAction.IssueStatusChanged,
                Description: "Status: Triage -> Open"),
        ];
        _issues.GetThreadAsync(id, Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(thread));

        var result = await _sut.Get(id);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var detail = ok.Value!;
        var threadProp = detail.GetType().GetProperty("thread")!.GetValue(detail);
        var threadList = threadProp.Should().BeAssignableTo<IEnumerable<object>>().Subject.ToList();
        threadList.Should().HaveCount(2);

        var first = threadList[0];
        first.GetType().GetProperty("type")!.GetValue(first).Should().Be("comment");
        first.GetType().GetProperty("content")!.GetValue(first).Should().Be("Still broken");
        first.GetType().GetProperty("actorIsReporter")!.GetValue(first).Should().Be(true);

        var second = threadList[1];
        second.GetType().GetProperty("type")!.GetValue(second).Should().Be("audit");
        second.GetType().GetProperty("action")!.GetValue(second).Should().Be("IssueStatusChanged");
    }

    // ==========================================================================
    // Writes — every one carries the key owner as actor (#1128)
    // ==========================================================================

    [HumansFact]
    public async Task Create_files_for_the_named_reporter_and_records_the_key_owner_as_filer()
    {
        var reporterId = Guid.NewGuid();
        var newIssueId = Guid.NewGuid();
        _issues.CreateIssueAsync(
                reporterId, IssueCategory.Bug, "T", "D", "Tickets", null, KeyOwnerId,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(newIssueId));

        var result = await _sut.Create(new ApiCreateIssueModel
        {
            ReporterUserId = reporterId,
            Category = IssueCategory.Bug,
            Title = "T",
            Description = "D",
            Section = "Tickets",
        });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value!.GetType().GetProperty("id")!.GetValue(ok.Value).Should().Be(newIssueId);

        // The reporter is caller-supplied and can be anyone; the filer is the key owner.
        await _issues.Received(1).CreateIssueAsync(
            reporterUserId: reporterId,
            category: IssueCategory.Bug,
            title: "T",
            description: "D",
            section: "Tickets",
            dueDate: null,
            actorUserId: KeyOwnerId,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PostComment_attributes_the_comment_to_the_key_owner()
    {
        // Reporter-vs-handler classification (and its auto-reopen) is the service's
        // invariant, derived from senderUserId — the controller only attributes.
        var issueId = Guid.NewGuid();
        _issues.PostCommentAsync(issueId, Arg.Any<IssueViewer>(), KeyOwnerId, "From the triage agent", false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IssueCommentResult(new IssueCommentInfo(
                Guid.NewGuid(), "From the triage agent", Instant.FromUtc(2026, 4, 29, 12, 0)))));

        var result = await _sut.PostComment(issueId, new PostIssueCommentModel { Content = "From the triage agent" });

        result.Should().BeOfType<OkObjectResult>();
        await _issues.Received(1).PostCommentAsync(
            issueId, Arg.Any<IssueViewer>(),
            senderUserId: KeyOwnerId,
            content: "From the triage agent",
            resolveOnPost: false,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PostComment_returns_NotFound_when_issue_missing()
    {
        var issueId = Guid.NewGuid();
        _issues.PostCommentAsync(issueId, Arg.Any<IssueViewer>(), KeyOwnerId, "Hello?", false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IssueCommentResult(null, NotFound: true)));

        var result = await _sut.PostComment(issueId, new PostIssueCommentModel { Content = "Hello?" });

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task UpdateStatus_records_the_key_owner_as_actor()
    {
        var issueId = Guid.NewGuid();
        _issues.UpdateStatusAsync(issueId, Arg.Any<IssueViewer>(), IssueStatus.Resolved, KeyOwnerId, Arg.Any<CancellationToken>())
            .Returns(IssueMutationResult.Success());

        var result = await _sut.UpdateStatus(issueId, new UpdateIssueStatusModel { Status = IssueStatus.Resolved });

        result.Should().BeOfType<OkObjectResult>();
        await _issues.Received(1).UpdateStatusAsync(
            issueId, Arg.Any<IssueViewer>(), IssueStatus.Resolved, actorUserId: KeyOwnerId, ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateStatus_returns_NotFound_when_service_reports_missing()
    {
        var issueId = Guid.NewGuid();
        _issues.UpdateStatusAsync(issueId, Arg.Any<IssueViewer>(), Arg.Any<IssueStatus>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(IssueMutationResult.Missing("Issue not found.")));

        var result = await _sut.UpdateStatus(issueId, new UpdateIssueStatusModel { Status = IssueStatus.Resolved });

        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// A rejected move is not a missing issue; every patch endpoint answers 422.
    /// </summary>
    [HumansFact]
    public async Task UpdateStatus_returns_422_when_the_service_rejects_the_move()
    {
        var issueId = Guid.NewGuid();
        _issues.UpdateStatusAsync(issueId, Arg.Any<IssueViewer>(), Arg.Any<IssueStatus>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(IssueMutationResult.Refused("Cannot reopen a closed issue")));

        var result = await _sut.UpdateStatus(issueId, new UpdateIssueStatusModel { Status = IssueStatus.Resolved });

        var body = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        body.Value.Should().BeEquivalentTo(new { error = "Cannot reopen a closed issue" });
    }

    [HumansTheory]
    [InlineData("comment")]
    [InlineData("status")]
    [InlineData("assignee")]
    [InlineData("section")]
    [InlineData("github")]
    public async Task Mutation_dependency_diagnostics_are_500_without_leaking_messages(string field)
    {
        var id = Guid.NewGuid();
        var failure = new InvalidOperationException("Persistence property not found: private diagnostic");
        _issues.PostCommentAsync(id, Arg.Any<IssueViewer>(), KeyOwnerId, "Body", false, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IssueCommentResult>(failure));
        _issues.UpdateStatusAsync(id, Arg.Any<IssueViewer>(), Arg.Any<IssueStatus>(), KeyOwnerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IssueMutationResult>(failure));
        _issues.UpdateAssigneeAsync(id, Arg.Any<IssueViewer>(), Arg.Any<Guid?>(), KeyOwnerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IssueMutationResult>(failure));
        _issues.UpdateSectionAsync(id, Arg.Any<IssueViewer>(), Arg.Any<string?>(), KeyOwnerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IssueMutationResult>(failure));
        _issues.SetGitHubIssueNumberAsync(id, Arg.Any<IssueViewer>(), Arg.Any<int?>(), KeyOwnerId, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<IssueMutationResult>(failure));

        var result = field switch
        {
            "comment" => await _sut.PostComment(id, new PostIssueCommentModel { Content = "Body" }),
            "status" => await _sut.UpdateStatus(id, new UpdateIssueStatusModel { Status = IssueStatus.Resolved }),
            "assignee" => await _sut.UpdateAssignee(id, new UpdateIssueAssigneeModel()),
            "section" => await _sut.UpdateSection(id, new UpdateIssueSectionModel()),
            _ => await _sut.SetGitHubIssue(id, new SetIssueGitHubIssueModel())
        };

        var response = result.Should().BeOfType<ObjectResult>().Subject;
        response.StatusCode.Should().Be(500);
        JsonSerializer.Serialize(response.Value).Should().NotContain(failure.Message);
    }

    [HumansFact]
    public async Task A_logged_service_failure_returns_500_instead_of_a_rule_refusal()
    {
        var id = Guid.NewGuid();
        _issues.UpdateStatusAsync(id, Arg.Any<IssueViewer>(), Arg.Any<IssueStatus>(), KeyOwnerId, Arg.Any<CancellationToken>())
            .Returns(IssueMutationResult.Failed("Failed to update status."));

        var result = await _sut.UpdateStatus(id, new UpdateIssueStatusModel { Status = IssueStatus.Resolved });

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(500);
    }

    // ==========================================================================
    // What the machine surface is allowed to see
    // ==========================================================================

    /// <summary>
    /// The queue is fetched as the key's owner, not as a full admin: a Board-only key holder
    /// sees exactly what the Issues UI would show that person.
    /// </summary>
    [HumansFact]
    public async Task List_reads_the_queue_as_the_key_owner()
    {
        WithRoles("Board");
        StubList();

        await _sut.List(status: null, category: null, section: null, assignee: null);

        await _issues.Received(1).GetIssueListAsync(
            Arg.Any<IssueListFilter>(),
            Arg.Is<IssueViewer>(v =>
                v.UserId == KeyOwnerId
                && v.Roles.SequenceEqual(new[] { "Board" })
                && !v.IsAdmin),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task An_admin_key_owner_reads_the_queue_as_an_admin()
    {
        WithRoles(RoleNames.Admin);
        StubList();

        await _sut.List(status: null, category: null, section: null, assignee: null);

        await _issues.Received(1).GetIssueListAsync(
            Arg.Any<IssueListFilter>(),
            Arg.Is<IssueViewer>(v => v.UserId == KeyOwnerId && v.IsAdmin),
            Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Each per-item route carries the same viewer the queue does, so a key cannot read or move
    /// issues its own queue would never have listed; refusing on it is Issues' job, and
    /// these pin that the controller hands it over.
    /// </summary>
    [HumansFact]
    public async Task Reading_one_issue_carries_the_key_owners_viewer()
    {
        WithRoles("Board");
        var issueId = Guid.NewGuid();
        _issues.GetIssueByIdAsync(issueId, Arg.Any<IssueViewer>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IssueDetail?>(null));

        await _sut.Get(issueId);

        await _issues.Received(1).GetIssueByIdAsync(issueId, BoardKeyOwner(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Commenting_carries_the_key_owners_viewer()
    {
        WithRoles("Board");
        var issueId = Guid.NewGuid();
        _issues.PostCommentAsync(
                issueId, Arg.Any<IssueViewer>(), Arg.Any<Guid?>(), Arg.Any<string>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IssueCommentResult(new IssueCommentInfo(
                Guid.NewGuid(), "Body", Instant.FromUtc(2026, 4, 29, 12, 0)))));

        await _sut.PostComment(issueId, new PostIssueCommentModel { Content = "Body" });

        await _issues.Received(1).PostCommentAsync(
            issueId, BoardKeyOwner(), KeyOwnerId, "Body",
            Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Patching_carries_the_key_owners_viewer()
    {
        WithRoles("Board");
        var issueId = Guid.NewGuid();

        await _sut.UpdateStatus(issueId, new UpdateIssueStatusModel { Status = IssueStatus.Resolved });

        await _issues.Received(1).UpdateStatusAsync(
            issueId, BoardKeyOwner(), IssueStatus.Resolved, KeyOwnerId, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Refusal is Issues' answer, not the controller's: an issue out of the key's reach comes
    /// back from the service as "not found", and the patch pipeline turns that into a 404.
    /// </summary>
    [HumansFact]
    public async Task A_patch_on_an_issue_out_of_reach_is_a_404()
    {
        WithRoles("Board");
        var issueId = Guid.NewGuid();
        _issues.UpdateStatusAsync(
                issueId, Arg.Any<IssueViewer>(), Arg.Any<IssueStatus>(), Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(IssueMutationResult.Missing("Issue not found.")));

        var result = await _sut.UpdateStatus(issueId, new UpdateIssueStatusModel { Status = IssueStatus.Resolved });

        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>The viewer <see cref="WithRoles"/>("Board") should produce.</summary>
    private static IssueViewer BoardKeyOwner() =>
        Arg.Is<IssueViewer>(v =>
            v.UserId == KeyOwnerId && v.Roles.SequenceEqual(new[] { "Board" }) && !v.IsAdmin);

    /// <summary>Re-installs the principal the auth filter would build for an owner in these roles.</summary>
    private void WithRoles(params string[] roleNames) =>
        _sut.ControllerContext.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, KeyOwnerId.ToString()),
                .. roleNames.Select(r => new Claim(ClaimTypes.Role, r))
            ],
            BackdoorApiKeyAuthFilter.AuthenticationScheme));

    /// <summary>
    /// The raw query value reaches a SQL <c>LIMIT</c> through <see cref="IssueListFilter"/>,
    /// so the controller clamps it.
    /// </summary>
    [HumansTheory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(50, 50)]
    [InlineData(5000, 1000)]
    public async Task List_clamps_the_limit(int requested, int expected)
    {
        IssueListFilter? captured = null;
        StubList(f => captured = f);

        await _sut.List(status: null, category: null, section: null, assignee: null, limit: requested);

        captured!.Limit.Should().Be(expected);
    }
}
