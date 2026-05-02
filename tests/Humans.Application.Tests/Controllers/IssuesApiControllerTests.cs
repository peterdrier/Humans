using AwesomeAssertions;
using Humans.Application.Interfaces.Issues;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using Humans.Web.Filters;
using Humans.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

#pragma warning disable CS0618 // Cross-domain navs are intentional in test fixtures.

namespace Humans.Application.Tests.Controllers;

/// <summary>
/// Unit tests for <see cref="IssuesApiController"/>. Mocks <see cref="IIssuesService"/>
/// and exercises the controller directly (no HTTP roundtrip / no WebApplicationFactory)
/// — the API key filter is tested separately by invoking <see cref="IssuesApiKeyAuthFilter.OnAuthorization"/>.
/// </summary>
public class IssuesApiControllerTests
{
    private readonly IIssuesService _issues = Substitute.For<IIssuesService>();
    private readonly IssuesApiController _sut;

    public IssuesApiControllerTests()
    {
        _sut = new IssuesApiController(_issues, NullLogger<IssuesApiController>.Instance);
    }

    private static Issue MakeIssue(
        Guid? id = null,
        IssueStatus status = IssueStatus.Open,
        IssueCategory category = IssueCategory.Bug,
        string? section = "Tickets",
        string title = "Issue title",
        string description = "Issue description",
        Guid? reporterId = null,
        string reporterName = "Reporter",
        string reporterEmail = "reporter@example.com",
        string reporterLanguage = "en")
    {
        var rId = reporterId ?? Guid.NewGuid();
        var reporter = new User
        {
            Id = rId,
            Email = reporterEmail,
            DisplayName = reporterName,
            PreferredLanguage = reporterLanguage
        };
        return new Issue
        {
            Id = id ?? Guid.NewGuid(),
            ReporterUserId = rId,
            Reporter = reporter,
            Section = section,
            Category = category,
            Title = title,
            Description = description,
            Status = status,
            CreatedAt = Instant.FromUtc(2026, 4, 29, 12, 0),
            UpdatedAt = Instant.FromUtc(2026, 4, 29, 12, 0)
        };
    }

    // ==========================================================================
    // List
    // ==========================================================================

    [HumansFact]
    public async Task List_returns_all_issues()
    {
        var issues = new[] { MakeIssue(), MakeIssue(), MakeIssue() };
        _issues
            .GetIssueListAsync(Arg.Any<IssueListFilter>(), Arg.Any<Guid>(),
                Arg.Any<IReadOnlyList<string>>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Issue>>(issues));

        var result = await _sut.List(status: null, category: null, section: null, assignee: null);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var list = ok.Value.Should().BeAssignableTo<IEnumerable<object>>().Subject;
        list.Should().HaveCount(3);
    }

    [HumansFact]
    public async Task List_filters_by_status()
    {
        IssueListFilter? captured = null;
        _issues
            .GetIssueListAsync(
                Arg.Do<IssueListFilter>(f => captured = f),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>()));

        await _sut.List(status: IssueStatus.Open, category: null, section: null, assignee: null);

        captured.Should().NotBeNull();
        captured!.Statuses.Should().BeEquivalentTo(new[] { IssueStatus.Open });
    }

    [HumansFact]
    public async Task List_filters_by_section()
    {
        IssueListFilter? captured = null;
        _issues
            .GetIssueListAsync(
                Arg.Do<IssueListFilter>(f => captured = f),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>()));

        await _sut.List(status: null, category: null, section: "Tickets", assignee: null);

        captured.Should().NotBeNull();
        captured!.Sections.Should().BeEquivalentTo(new string?[] { "Tickets" });
    }

    [HumansFact]
    public async Task List_filters_by_reporter()
    {
        IssueListFilter? captured = null;
        _issues
            .GetIssueListAsync(
                Arg.Do<IssueListFilter>(f => captured = f),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>()));

        var reporterId = Guid.NewGuid();
        await _sut.List(
            status: null, category: null, section: null, assignee: null,
            reporter: reporterId);

        captured.Should().NotBeNull();
        captured!.ReporterUserId.Should().Be(reporterId);
    }

    [HumansFact]
    public async Task List_filters_by_search_text()
    {
        IssueListFilter? captured = null;
        _issues
            .GetIssueListAsync(
                Arg.Do<IssueListFilter>(f => captured = f),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>()));

        await _sut.List(
            status: null, category: null, section: null, assignee: null,
            search: "duplicate");

        captured.Should().NotBeNull();
        captured!.SearchText.Should().Be("duplicate");
    }

    [HumansFact]
    public async Task List_treats_blank_search_as_unset()
    {
        IssueListFilter? captured = null;
        _issues
            .GetIssueListAsync(
                Arg.Do<IssueListFilter>(f => captured = f),
                Arg.Any<Guid>(), Arg.Any<IReadOnlyList<string>>(),
                Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Issue>>(Array.Empty<Issue>()));

        await _sut.List(
            status: null, category: null, section: null, assignee: null,
            search: "   ");

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
            .Returns(Task.FromResult<Issue?>(null));

        var result = await _sut.Get(id);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task Get_includes_thread_with_comments_and_audit_events()
    {
        var issue = MakeIssue();
        _issues.GetIssueByIdAsync(issue.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Issue?>(issue));

        var thread = new IssueThreadEvent[]
        {
            new IssueCommentEvent(
                CommentId: Guid.NewGuid(),
                At: Instant.FromUtc(2026, 4, 29, 12, 5),
                ActorUserId: issue.ReporterUserId,
                ActorDisplayName: "Reporter",
                ActorIsReporter: true,
                Content: "Still broken"),
            new IssueAuditEvent(
                At: Instant.FromUtc(2026, 4, 29, 12, 10),
                ActorUserId: Guid.NewGuid(),
                ActorDisplayName: "Admin",
                Action: AuditAction.IssueStatusChanged,
                Description: "Status: Triage -> Open"),
        };
        _issues.GetThreadAsync(issue.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IssueThreadEvent>>(thread));

        var result = await _sut.Get(issue.Id);

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        var detail = ok.Value!;
        var threadProp = detail.GetType().GetProperty("thread")!.GetValue(detail);
        threadProp.Should().BeAssignableTo<IEnumerable<object>>();
        var threadList = ((IEnumerable<object>)threadProp!).ToList();
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
    // Create
    // ==========================================================================

    [HumansFact]
    public async Task Create_creates_issue_with_specified_reporter_and_returns_Id()
    {
        var reporterId = Guid.NewGuid();
        var newIssueId = Guid.NewGuid();
        Issue created = MakeIssue(id: newIssueId, reporterId: reporterId);

        _issues.SubmitIssueAsync(
                reporterId, IssueCategory.Bug, "T", "D",
                "Tickets", null, null, null, null, null,
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(created));

        var result = await _sut.Create(new ApiCreateIssueModel
        {
            ReporterUserId = reporterId,
            Category = IssueCategory.Bug,
            Title = "T",
            Description = "D",
            Section = "Tickets"
        });

        var ok = result.Should().BeOfType<OkObjectResult>().Subject;
        ok.Value!.GetType().GetProperty("id")!.GetValue(ok.Value).Should().Be(newIssueId);

        await _issues.Received(1).SubmitIssueAsync(
            reporterId, IssueCategory.Bug, "T", "D",
            "Tickets", null, null, null, null, null,
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // PostComment
    // ==========================================================================

    [HumansFact]
    public async Task PostComment_sets_SenderUserId_null_for_keyed_path()
    {
        var issueId = Guid.NewGuid();
        var comment = new IssueComment
        {
            Id = Guid.NewGuid(),
            IssueId = issueId,
            Content = "From admin agent",
            CreatedAt = Instant.FromUtc(2026, 4, 29, 12, 0)
        };
        _issues.PostCommentAsync(issueId, null, "From admin agent", false, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(comment));

        var result = await _sut.PostComment(issueId, new PostIssueCommentModel { Content = "From admin agent" });

        result.Should().BeOfType<OkObjectResult>();
        await _issues.Received(1).PostCommentAsync(
            issueId,
            senderUserId: null,
            content: "From admin agent",
            senderIsReporter: false,
            ct: Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // UpdateStatus
    // ==========================================================================

    [HumansFact]
    public async Task UpdateStatus_logs_audit_with_API_actor()
    {
        var issueId = Guid.NewGuid();
        _issues.UpdateStatusAsync(issueId, IssueStatus.Resolved, null, Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        var result = await _sut.UpdateStatus(issueId, new UpdateIssueStatusModel { Status = IssueStatus.Resolved });

        result.Should().BeOfType<OkObjectResult>();
        await _issues.Received(1).UpdateStatusAsync(
            issueId, IssueStatus.Resolved,
            actorUserId: null,
            ct: Arg.Any<CancellationToken>());
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

    // ==========================================================================
    // ApiKey filter behavior
    // ==========================================================================

    [HumansFact]
    public void ApiKey_missing_returns_503_when_settings_have_empty_key()
    {
        var filter = new IssuesApiKeyAuthFilter(Options.Create(new IssuesApiSettings { ApiKey = string.Empty }));
        var ctx = MakeAuthFilterContext(headerKey: null);

        filter.OnAuthorization(ctx);

        ctx.Result.Should().BeOfType<StatusCodeResult>()
            .Which.StatusCode.Should().Be(503);
    }

    [HumansFact]
    public void ApiKey_wrong_returns_401_when_settings_configured()
    {
        var filter = new IssuesApiKeyAuthFilter(Options.Create(new IssuesApiSettings { ApiKey = "right-key" }));
        var ctx = MakeAuthFilterContext(headerKey: "wrong-key");

        filter.OnAuthorization(ctx);

        ctx.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [HumansFact]
    public void ApiKey_matching_passes_through()
    {
        var filter = new IssuesApiKeyAuthFilter(Options.Create(new IssuesApiSettings { ApiKey = "right-key" }));
        var ctx = MakeAuthFilterContext(headerKey: "right-key");

        filter.OnAuthorization(ctx);

        ctx.Result.Should().BeNull();
    }

    private static AuthorizationFilterContext MakeAuthFilterContext(string? headerKey)
    {
        var http = new DefaultHttpContext();
        if (headerKey is not null)
        {
            http.Request.Headers["X-Api-Key"] = headerKey;
        }
        var actionContext = new ActionContext(
            http,
            new Microsoft.AspNetCore.Routing.RouteData(),
            new Microsoft.AspNetCore.Mvc.Abstractions.ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, Array.Empty<IFilterMetadata>());
    }
}
