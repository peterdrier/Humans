using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Filters;
using Humans.Issues.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using System.Security.Claims;

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

    public BackdoorIssuesControllerTests()
    {
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
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IssueListSnapshot>>([]));

    // ==========================================================================
    // List
    // ==========================================================================

    [HumansFact]
    public async Task List_returns_all_issues()
    {
        IReadOnlyList<IssueListSnapshot> issues = [MakeSnapshot(), MakeSnapshot(), MakeSnapshot()];
        _issues
            .GetIssueListAsync(Arg.Any<IssueListFilter>(), Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
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
        _issues.GetIssueByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IssueDetail?>(null));

        var result = await _sut.Get(id);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Get_includes_thread_with_comments_and_audit_events()
    {
        var id = Guid.NewGuid();
        var reporterId = Guid.NewGuid();
        _issues.GetIssueByIdAsync(id, Arg.Any<CancellationToken>())
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
        _issues.GetThreadAsync(id, Arg.Any<CancellationToken>()).Returns(Task.FromResult(thread));

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
        var issueId = Guid.NewGuid();
        _issues.PostCommentAsync(issueId, KeyOwnerId, "From the triage agent", false, false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new IssueCommentInfo(
                Guid.NewGuid(), "From the triage agent", Instant.FromUtc(2026, 4, 29, 12, 0))));

        var result = await _sut.PostComment(issueId, new PostIssueCommentModel { Content = "From the triage agent" });

        result.Should().BeOfType<OkObjectResult>();
        await _issues.Received(1).PostCommentAsync(
            issueId,
            senderUserId: KeyOwnerId,
            content: "From the triage agent",
            senderIsReporter: false,
            resolveOnPost: false,
            ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateStatus_records_the_key_owner_as_actor()
    {
        var issueId = Guid.NewGuid();
        _issues.UpdateStatusAsync(issueId, IssueStatus.Resolved, KeyOwnerId, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.UpdateStatus(issueId, new UpdateIssueStatusModel { Status = IssueStatus.Resolved });

        result.Should().BeOfType<OkObjectResult>();
        await _issues.Received(1).UpdateStatusAsync(
            issueId, IssueStatus.Resolved, actorUserId: KeyOwnerId, ct: Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateStatus_returns_NotFound_when_service_throws_invalid_op()
    {
        var issueId = Guid.NewGuid();
        _issues.UpdateStatusAsync(issueId, Arg.Any<IssueStatus>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("not found")));

        var result = await _sut.UpdateStatus(issueId, new UpdateIssueStatusModel { Status = IssueStatus.Resolved });

        result.Should().BeOfType<NotFoundResult>();
    }
}
