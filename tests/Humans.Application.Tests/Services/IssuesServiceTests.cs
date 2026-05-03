using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Issues;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;
using IssuesApplicationService = Humans.Application.Services.Issues.IssuesService;

namespace Humans.Application.Tests.Services;

public class IssuesServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly IEmailService _emailService;
    private readonly IAuditLogService _auditLog;
    private readonly IUserService _userService;
    private readonly IUserEmailService _userEmailService;
    private readonly IRoleAssignmentService _roleService;
    private readonly INotificationService _notificationService;
    private readonly INavBadgeCacheInvalidator _navBadge;
    private readonly IIssuesBadgeCacheInvalidator _issuesBadge;
    private readonly IMemoryCache _cache;
    private readonly IIssuesRepository _repository;
    private readonly IssuesApplicationService _service;

    public IssuesServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 29, 12, 0));
        _emailService = Substitute.For<IEmailService>();
        _auditLog = Substitute.For<IAuditLogService>();
        _auditLog
            .GetFilteredEntriesAsync(
                Arg.Any<string?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyList<AuditAction>?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AuditLogEntry>>([]));

        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());

        _userService = Substitute.For<IUserService>();
        _userService
            .GetByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = (IReadOnlyCollection<Guid>)call[0]!;
                var dict = _dbContext.Users
                    .AsNoTracking()
                    .Where(u => ids.Contains(u.Id))
                    .ToDictionary(u => u.Id);
                return Task.FromResult<IReadOnlyDictionary<Guid, User>>(dict);
            });
        _userService
            .GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = (Guid)call[0]!;
                return Task.FromResult(_dbContext.Users.AsNoTracking().FirstOrDefault(u => u.Id == id));
            });

        _userEmailService = Substitute.For<IUserEmailService>();
        _userEmailService
            .GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = (IReadOnlyCollection<Guid>)call[0]!;
                IReadOnlyDictionary<Guid, string> dict = _dbContext.Users
                    .AsNoTracking()
                    .Where(u => ids.Contains(u.Id) && u.Email != null)
                    .ToDictionary(u => u.Id, u => u.Email!);
                return Task.FromResult(dict);
            });

        _roleService = Substitute.For<IRoleAssignmentService>();
        _roleService
            .GetActiveUserIdsInRoleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([]));

        _notificationService = Substitute.For<INotificationService>();
        _navBadge = Substitute.For<INavBadgeCacheInvalidator>();
        _issuesBadge = Substitute.For<IIssuesBadgeCacheInvalidator>();
        _cache = new MemoryCache(new MemoryCacheOptions());

        _repository = new IssuesRepository(new TestDbContextFactory(options));

        _service = new IssuesApplicationService(
            _repository, _userService, _userEmailService, _roleService,
            _emailService, _notificationService, _auditLog, _navBadge,
            _issuesBadge, _cache,
            _clock, env, NullLogger<IssuesApplicationService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ==========================================================================
    // SubmitIssueAsync
    // ==========================================================================

    [HumansFact]
    public async Task SubmitIssueAsync_lands_in_Triage_and_invalidates_nav_badge()
    {
        var userId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = userId, Email = "u@u.com", DisplayName = "U" });
        await _dbContext.SaveChangesAsync();

        var issue = await _service.SubmitIssueAsync(
            userId, IssueCategory.Bug, "Title", "Desc",
            section: IssueSectionRouting.Tickets,
            pageUrl: "/Tickets", userAgent: null, additionalContext: null,
            screenshot: null);

        issue.Status.Should().Be(IssueStatus.Triage);
        issue.ReporterUserId.Should().Be(userId);
        issue.Section.Should().Be(IssueSectionRouting.Tickets);
        _navBadge.Received(1).Invalidate();

        var stored = await _dbContext.Issues.AsNoTracking().FirstAsync(i => i.Id == issue.Id);
        stored.Status.Should().Be(IssueStatus.Triage);
    }

    [HumansFact]
    public async Task SubmitIssueAsync_notifies_admins_and_section_handlers_excluding_reporter()
    {
        var reporterId = Guid.NewGuid();
        var adminId = Guid.NewGuid();
        var ticketAdminId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = reporterId, Email = "r@x", DisplayName = "R" });
        await _dbContext.SaveChangesAsync();

        _roleService
            .GetActiveUserIdsInRoleAsync(RoleNames.Admin, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([adminId, reporterId]));
        _roleService
            .GetActiveUserIdsInRoleAsync(RoleNames.TicketAdmin, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([ticketAdminId]));

        IReadOnlyList<Guid>? capturedRecipients = null;
        await _notificationService
            .SendAsync(
                NotificationSource.IssueSubmitted,
                Arg.Any<NotificationClass>(),
                Arg.Any<NotificationPriority>(),
                Arg.Any<string>(),
                Arg.Do<IReadOnlyList<Guid>>(r => capturedRecipients = r),
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(),
                Arg.Any<CancellationToken>());

        await _service.SubmitIssueAsync(
            reporterId, IssueCategory.Bug, "Title", "Desc",
            section: IssueSectionRouting.Tickets,
            pageUrl: "/Tickets", userAgent: null, additionalContext: null,
            screenshot: null);

        capturedRecipients.Should().NotBeNull();
        capturedRecipients.Should().BeEquivalentTo(new[] { adminId, ticketAdminId });
        capturedRecipients.Should().NotContain(reporterId);
    }

    [HumansFact]
    public async Task SubmitIssueAsync_with_oversized_screenshot_throws()
    {
        var userId = Guid.NewGuid();
        var screenshot = Substitute.For<IFormFile>();
        screenshot.Length.Returns(20L * 1024 * 1024);
        screenshot.ContentType.Returns("image/png");

        var act = async () => await _service.SubmitIssueAsync(
            userId, IssueCategory.Bug, "T", "D",
            section: null, pageUrl: null, userAgent: null, additionalContext: null,
            screenshot: screenshot);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*10MB*");
    }

    [HumansFact]
    public async Task SubmitIssueAsync_with_disallowed_content_type_throws()
    {
        var userId = Guid.NewGuid();
        var screenshot = Substitute.For<IFormFile>();
        screenshot.Length.Returns(1024L);
        screenshot.ContentType.Returns("application/pdf");

        var act = async () => await _service.SubmitIssueAsync(
            userId, IssueCategory.Bug, "T", "D",
            section: null, pageUrl: null, userAgent: null, additionalContext: null,
            screenshot: screenshot);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*JPEG, PNG, or WebP*");
    }

    // ==========================================================================
    // PostCommentAsync
    // ==========================================================================

    [HumansFact]
    public async Task PostCommentAsync_reporter_on_terminal_auto_reopens_to_Open()
    {
        var (reporterId, issueId) = await SeedIssueAsync(IssueStatus.Resolved);

        await _service.PostCommentAsync(issueId, reporterId, "Still broken", senderIsReporter: true);

        var stored = await _dbContext.Issues.AsNoTracking().FirstAsync(i => i.Id == issueId);
        stored.Status.Should().Be(IssueStatus.Open);
    }

    [HumansFact]
    public async Task PostCommentAsync_reporter_on_terminal_clears_resolved_fields()
    {
        var (reporterId, issueId) = await SeedIssueAsync(IssueStatus.WontFix, withResolvedFields: true);

        await _service.PostCommentAsync(issueId, reporterId, "Reopen please", senderIsReporter: true);

        var stored = await _dbContext.Issues.AsNoTracking().FirstAsync(i => i.Id == issueId);
        stored.ResolvedAt.Should().BeNull();
        stored.ResolvedByUserId.Should().BeNull();
    }

    [HumansFact]
    public async Task PostCommentAsync_reporter_on_open_does_not_change_status()
    {
        var (reporterId, issueId) = await SeedIssueAsync(IssueStatus.Open);

        await _service.PostCommentAsync(issueId, reporterId, "Update", senderIsReporter: true);

        var stored = await _dbContext.Issues.AsNoTracking().FirstAsync(i => i.Id == issueId);
        stored.Status.Should().Be(IssueStatus.Open);
    }

    [HumansFact]
    public async Task PostCommentAsync_handler_sends_email_and_notification_to_reporter()
    {
        var reporterId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = reporterId,
            Email = "reporter@test.com",
            DisplayName = "Reporter",
            PreferredLanguage = "en"
        });
        await _dbContext.SaveChangesAsync();

        var issueId = await SeedIssueRowAsync(reporterId, IssueStatus.Open, "Report Title");
        var adminId = Guid.NewGuid();

        await _service.PostCommentAsync(issueId, adminId, "Looking at it", senderIsReporter: false);

        await _emailService.Received(1).SendIssueCommentAsync(
            "reporter@test.com",
            "Reporter",
            "Report Title",
            "Looking at it",
            $"/Issues/{issueId}",
            "en",
            Arg.Any<CancellationToken>());

        await _notificationService.Received().SendAsync(
            NotificationSource.IssueComment,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(reporterId)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task PostCommentAsync_reporter_does_not_email_self()
    {
        var (reporterId, issueId) = await SeedIssueAsync(IssueStatus.Open);

        await _service.PostCommentAsync(issueId, reporterId, "More info", senderIsReporter: true);

        await _emailService.DidNotReceive().SendIssueCommentAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // UpdateStatusAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpdateStatusAsync_to_terminal_sets_ResolvedAt_and_ResolvedByUserId()
    {
        var (_, issueId) = await SeedIssueAsync(IssueStatus.Open);
        var actorId = Guid.NewGuid();

        await _service.UpdateStatusAsync(issueId, IssueStatus.Resolved, actorId);

        var stored = await _dbContext.Issues.AsNoTracking().FirstAsync(i => i.Id == issueId);
        stored.Status.Should().Be(IssueStatus.Resolved);
        stored.ResolvedAt.Should().NotBeNull();
        stored.ResolvedByUserId.Should().Be(actorId);
    }

    [HumansFact]
    public async Task UpdateStatusAsync_from_terminal_to_nonterminal_clears_resolved()
    {
        var (_, issueId) = await SeedIssueAsync(IssueStatus.Open);
        var actorId = Guid.NewGuid();

        await _service.UpdateStatusAsync(issueId, IssueStatus.Resolved, actorId);
        await _service.UpdateStatusAsync(issueId, IssueStatus.Open, actorId);

        var stored = await _dbContext.Issues.AsNoTracking().FirstAsync(i => i.Id == issueId);
        stored.Status.Should().Be(IssueStatus.Open);
        stored.ResolvedAt.Should().BeNull();
        stored.ResolvedByUserId.Should().BeNull();
    }

    [HumansFact]
    public async Task UpdateStatusAsync_no_change_returns_without_audit()
    {
        var (_, issueId) = await SeedIssueAsync(IssueStatus.Open);
        var actorId = Guid.NewGuid();

        await _service.UpdateStatusAsync(issueId, IssueStatus.Open, actorId);

        await _auditLog.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdateStatusAsync_notifies_reporter_and_assignee_excluding_actor()
    {
        var (reporterId, issueId) = await SeedIssueAsync(IssueStatus.Open);

        // Stamp an assignee on the issue so the dispatch helper has somebody
        // to notify alongside the reporter.
        var assigneeId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = assigneeId, Email = "ass@x.com", DisplayName = "Assignee" });
        await _dbContext.SaveChangesAsync();
        await _service.UpdateAssigneeAsync(issueId, assigneeId, Guid.NewGuid());
        _notificationService.ClearReceivedCalls();

        // Actor is the assignee — they should NOT receive a notification, but
        // the reporter still should.
        await _service.UpdateStatusAsync(issueId, IssueStatus.Resolved, assigneeId);

        await _notificationService.Received(1).SendAsync(
            NotificationSource.IssueStatusChanged,
            NotificationClass.Informational,
            NotificationPriority.Normal,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids =>
                ids.Contains(reporterId) && !ids.Contains(assigneeId) && ids.Count == 1),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateStatusAsync_short_circuits_when_actor_is_only_recipient()
    {
        // Reporter flips their own issue's status with no assignee → recipient
        // set is empty → no notification is dispatched.
        var (reporterId, issueId) = await SeedIssueAsync(IssueStatus.Open);

        await _service.UpdateStatusAsync(issueId, IssueStatus.Resolved, reporterId);

        await _notificationService.DidNotReceive().SendAsync(
            NotificationSource.IssueStatusChanged,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // UpdateAssigneeAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpdateAssigneeAsync_audit_includes_old_and_new_names()
    {
        var (_, issueId) = await SeedIssueAsync(IssueStatus.Open);
        var oldAssignee = new User { Id = Guid.NewGuid(), DisplayName = "Old Person", Email = "old@x.com" };
        var newAssignee = new User { Id = Guid.NewGuid(), DisplayName = "New Person", Email = "new@x.com" };
        _dbContext.Users.Add(oldAssignee);
        _dbContext.Users.Add(newAssignee);
        await _dbContext.SaveChangesAsync();

        var actorId = Guid.NewGuid();
        await _service.UpdateAssigneeAsync(issueId, oldAssignee.Id, actorId);
        await _service.UpdateAssigneeAsync(issueId, newAssignee.Id, actorId);

        await _auditLog.Received().LogAsync(
            AuditAction.IssueAssigneeChanged,
            nameof(Issue),
            issueId,
            Arg.Is<string>(s => s.Contains("Old Person", StringComparison.Ordinal)
                                && s.Contains("New Person", StringComparison.Ordinal)),
            actorId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task UpdateAssigneeAsync_notifies_new_assignee()
    {
        var (_, issueId) = await SeedIssueAsync(IssueStatus.Open);
        var newAssigneeId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = newAssigneeId, Email = "a@a.com", DisplayName = "Assignee" });
        await _dbContext.SaveChangesAsync();

        await _service.UpdateAssigneeAsync(issueId, newAssigneeId, Guid.NewGuid());

        await _notificationService.Received().SendAsync(
            NotificationSource.IssueAssigned,
            NotificationClass.Actionable,
            NotificationPriority.Normal,
            Arg.Any<string>(),
            Arg.Is<IReadOnlyList<Guid>>(ids => ids.Contains(newAssigneeId)),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateAssigneeAsync_self_assign_does_not_notify_actor()
    {
        var (_, issueId) = await SeedIssueAsync(IssueStatus.Open);
        var actorId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = actorId, Email = "self@x.com", DisplayName = "Self" });
        await _dbContext.SaveChangesAsync();

        await _service.UpdateAssigneeAsync(issueId, actorId, actorId);

        await _notificationService.DidNotReceive().SendAsync(
            NotificationSource.IssueAssigned,
            Arg.Any<NotificationClass>(),
            Arg.Any<NotificationPriority>(),
            Arg.Any<string>(),
            Arg.Any<IReadOnlyList<Guid>>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // UpdateSectionAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpdateSectionAsync_audits_change_and_invalidates_nav_badge()
    {
        var (_, issueId) = await SeedIssueAsync(IssueStatus.Open, section: IssueSectionRouting.Tickets);
        _navBadge.ClearReceivedCalls();
        var actorId = Guid.NewGuid();

        await _service.UpdateSectionAsync(issueId, IssueSectionRouting.Teams, actorId);

        var stored = await _dbContext.Issues.AsNoTracking().FirstAsync(i => i.Id == issueId);
        stored.Section.Should().Be(IssueSectionRouting.Teams);

        _navBadge.Received(1).Invalidate();

        await _auditLog.Received().LogAsync(
            AuditAction.IssueSectionChanged,
            nameof(Issue),
            issueId,
            Arg.Is<string>(s => s.Contains("Tickets", StringComparison.Ordinal)
                                && s.Contains("Teams", StringComparison.Ordinal)),
            actorId,
            Arg.Any<Guid?>(),
            Arg.Any<string?>());
    }

    // ==========================================================================
    // GetIssueListAsync visibility filter
    // ==========================================================================

    [HumansFact]
    public async Task GetIssueListAsync_admin_passes_null_section_filter()
    {
        var repo = Substitute.For<IIssuesRepository>();
        repo.GetListAsync(
                Arg.Any<IssueListFilter>(),
                Arg.Any<IReadOnlySet<string>?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Issue>>([]));

        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var svc = new IssuesApplicationService(
            repo, _userService, _userEmailService, _roleService,
            _emailService, _notificationService, _auditLog, _navBadge,
            _issuesBadge, _cache,
            _clock, env, NullLogger<IssuesApplicationService>.Instance);

        await svc.GetIssueListAsync(new IssueListFilter(), Guid.NewGuid(), [], viewerIsAdmin: true);

        await repo.Received(1).GetListAsync(
            Arg.Any<IssueListFilter>(),
            Arg.Is<IReadOnlySet<string>?>(s => s == null),
            Arg.Is<Guid?>(g => g == null),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetIssueListAsync_role_holder_passes_section_filter_and_reporter_fallback()
    {
        var repo = Substitute.For<IIssuesRepository>();
        repo.GetListAsync(
                Arg.Any<IssueListFilter>(),
                Arg.Any<IReadOnlySet<string>?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Issue>>([]));

        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var svc = new IssuesApplicationService(
            repo, _userService, _userEmailService, _roleService,
            _emailService, _notificationService, _auditLog, _navBadge,
            _issuesBadge, _cache,
            _clock, env, NullLogger<IssuesApplicationService>.Instance);

        var viewerId = Guid.NewGuid();
        await svc.GetIssueListAsync(
            new IssueListFilter(), viewerId,
            [RoleNames.TicketAdmin],
            viewerIsAdmin: false);

        await repo.Received(1).GetListAsync(
            Arg.Any<IssueListFilter>(),
            Arg.Is<IReadOnlySet<string>?>(s => s != null && s.Contains(IssueSectionRouting.Tickets)),
            Arg.Is<Guid?>(g => g == viewerId),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetIssueListAsync_no_roles_filters_to_own_reports_only()
    {
        var repo = Substitute.For<IIssuesRepository>();
        repo.GetListAsync(
                Arg.Any<IssueListFilter>(),
                Arg.Any<IReadOnlySet<string>?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Issue>>([]));

        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var svc = new IssuesApplicationService(
            repo, _userService, _userEmailService, _roleService,
            _emailService, _notificationService, _auditLog, _navBadge,
            _issuesBadge, _cache,
            _clock, env, NullLogger<IssuesApplicationService>.Instance);

        var viewerId = Guid.NewGuid();
        await svc.GetIssueListAsync(
            new IssueListFilter(), viewerId,
            viewerRoles: [],
            viewerIsAdmin: false);

        await repo.Received(1).GetListAsync(
            Arg.Any<IssueListFilter>(),
            Arg.Is<IReadOnlySet<string>?>(s => s != null && s.Count == 0),
            Arg.Is<Guid?>(g => g == viewerId),
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // GetActionableCountForViewerAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetActionableCountForViewerAsync_admin_passes_null_filter()
    {
        var repo = Substitute.For<IIssuesRepository>();
        repo.CountActionableAsync(
                Arg.Any<IReadOnlySet<string>?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var svc = new IssuesApplicationService(
            repo, _userService, _userEmailService, _roleService,
            _emailService, _notificationService, _auditLog, _navBadge,
            _issuesBadge, _cache,
            _clock, env, NullLogger<IssuesApplicationService>.Instance);

        await svc.GetActionableCountForViewerAsync(
            Guid.NewGuid(), [], viewerIsAdmin: true);

        await repo.Received(1).CountActionableAsync(
            Arg.Is<IReadOnlySet<string>?>(s => s == null),
            Arg.Is<Guid?>(g => g == null),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetActionableCountForViewerAsync_section_owner_passes_section_set()
    {
        var repo = Substitute.For<IIssuesRepository>();
        repo.CountActionableAsync(
                Arg.Any<IReadOnlySet<string>?>(),
                Arg.Any<Guid?>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(0));

        var env = Substitute.For<IHostEnvironment>();
        env.ContentRootPath.Returns(Path.GetTempPath());
        var svc = new IssuesApplicationService(
            repo, _userService, _userEmailService, _roleService,
            _emailService, _notificationService, _auditLog, _navBadge,
            _issuesBadge, _cache,
            _clock, env, NullLogger<IssuesApplicationService>.Instance);

        var viewerId = Guid.NewGuid();
        await svc.GetActionableCountForViewerAsync(
            viewerId, [RoleNames.TeamsAdmin], viewerIsAdmin: false);

        await repo.Received(1).CountActionableAsync(
            Arg.Is<IReadOnlySet<string>?>(s => s != null && s.Contains(IssueSectionRouting.Teams)),
            Arg.Is<Guid?>(g => g == viewerId),
            Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // GDPR contributor
    // ==========================================================================

    [HumansFact]
    public async Task ContributeForUserAsync_returns_only_user_own_issues()
    {
        var aliceId = Guid.NewGuid();
        var bobId = Guid.NewGuid();
        _dbContext.Users.Add(new User { Id = aliceId, Email = "a@a.com", DisplayName = "Alice" });
        _dbContext.Users.Add(new User { Id = bobId, Email = "b@b.com", DisplayName = "Bob" });
        await _dbContext.SaveChangesAsync();

        await SeedIssueRowAsync(aliceId, IssueStatus.Open, "Alice's first");
        await SeedIssueRowAsync(aliceId, IssueStatus.Open, "Alice's second");
        await SeedIssueRowAsync(bobId, IssueStatus.Open, "Bob's");

        var slices = await _service.ContributeForUserAsync(aliceId, default);

        slices.Should().ContainSingle();
        slices[0].SectionName.Should().Be("Issues");
        var data = slices[0].Data.Should().BeAssignableTo<System.Collections.IEnumerable>().Subject;
        data.Cast<object>().Should().HaveCount(2);
    }

    // ==========================================================================
    // Helpers
    // ==========================================================================

    private async Task<(Guid reporterId, Guid issueId)> SeedIssueAsync(
        IssueStatus status,
        bool withResolvedFields = false,
        string? section = null)
    {
        var reporterId = Guid.NewGuid();
        if (!_dbContext.Users.Any(u => u.Id == reporterId))
        {
            _dbContext.Users.Add(new User
            {
                Id = reporterId,
                Email = $"{reporterId}@test.com",
                DisplayName = "Reporter",
                PreferredLanguage = "en"
            });
            await _dbContext.SaveChangesAsync();
        }

        var issueId = await SeedIssueRowAsync(reporterId, status, "Title", section, withResolvedFields);
        return (reporterId, issueId);
    }

    private async Task<Guid> SeedIssueRowAsync(
        Guid reporterId,
        IssueStatus status,
        string title,
        string? section = null,
        bool withResolvedFields = false)
    {
        var now = _clock.GetCurrentInstant();
        var issue = new Issue
        {
            Id = Guid.NewGuid(),
            ReporterUserId = reporterId,
            Section = section,
            Category = IssueCategory.Bug,
            Title = title,
            Description = "Description",
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            ResolvedAt = withResolvedFields ? now : null,
            ResolvedByUserId = withResolvedFields ? Guid.NewGuid() : null
        };
        _dbContext.Issues.Add(issue);
        await _dbContext.SaveChangesAsync();
        return issue.Id;
    }

    private async Task<Guid> SeedIssueWithResolvedAtAsync(
        IssueStatus status, Instant resolvedAt, string? screenshotPath = null)
    {
        var reporterId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = reporterId,
            Email = $"{reporterId}@test.com",
            DisplayName = "Reporter",
            PreferredLanguage = "en"
        });
        var issue = new Issue
        {
            Id = Guid.NewGuid(),
            ReporterUserId = reporterId,
            Category = IssueCategory.Bug,
            Title = "Title",
            Description = "Description",
            Status = status,
            CreatedAt = resolvedAt,
            UpdatedAt = resolvedAt,
            ResolvedAt = status.IsTerminal() ? resolvedAt : null,
            ScreenshotStoragePath = screenshotPath
        };
        _dbContext.Issues.Add(issue);
        await _dbContext.SaveChangesAsync();
        return issue.Id;
    }

    // ==========================================================================
    // PurgeExpiredAsync — 6-month retention sweep used by CleanupIssuesJob
    // ==========================================================================

    [HumansFact]
    public async Task PurgeExpiredAsync_DeletesTerminalIssuesOlderThanSixMonths()
    {
        var now = _clock.GetCurrentInstant();
        var oldResolved = now - Duration.FromDays(181);

        var toPurge = await SeedIssueWithResolvedAtAsync(IssueStatus.Resolved, oldResolved);
        var alsoPurgeWontFix = await SeedIssueWithResolvedAtAsync(IssueStatus.WontFix, oldResolved);
        var alsoPurgeDuplicate = await SeedIssueWithResolvedAtAsync(IssueStatus.Duplicate, oldResolved);

        var deleted = await _service.PurgeExpiredAsync();

        deleted.Should().Be(3);
        _dbContext.Issues.Any(i => i.Id == toPurge).Should().BeFalse();
        _dbContext.Issues.Any(i => i.Id == alsoPurgeWontFix).Should().BeFalse();
        _dbContext.Issues.Any(i => i.Id == alsoPurgeDuplicate).Should().BeFalse();
    }

    [HumansFact]
    public async Task PurgeExpiredAsync_KeepsTerminalIssuesYoungerThanSixMonths()
    {
        var now = _clock.GetCurrentInstant();
        var recentResolved = now - Duration.FromDays(179);

        var keep = await SeedIssueWithResolvedAtAsync(IssueStatus.Resolved, recentResolved);

        var deleted = await _service.PurgeExpiredAsync();

        deleted.Should().Be(0);
        _dbContext.Issues.Any(i => i.Id == keep).Should().BeTrue();
    }

    [HumansFact]
    public async Task PurgeExpiredAsync_NeverDeletesNonTerminalIssuesEvenWhenAncient()
    {
        var ancient = _clock.GetCurrentInstant() - Duration.FromDays(365 * 5);

        var keepOpen = await SeedIssueWithResolvedAtAsync(IssueStatus.Open, ancient);
        var keepInProgress = await SeedIssueWithResolvedAtAsync(IssueStatus.InProgress, ancient);
        var keepTriage = await SeedIssueWithResolvedAtAsync(IssueStatus.Triage, ancient);

        var deleted = await _service.PurgeExpiredAsync();

        deleted.Should().Be(0);
        _dbContext.Issues.Any(i => i.Id == keepOpen).Should().BeTrue();
        _dbContext.Issues.Any(i => i.Id == keepInProgress).Should().BeTrue();
        _dbContext.Issues.Any(i => i.Id == keepTriage).Should().BeTrue();
    }

    [HumansFact]
    public async Task PurgeExpiredAsync_DeletesScreenshotDirectory()
    {
        var oldResolved = _clock.GetCurrentInstant() - Duration.FromDays(200);

        var issueId = Guid.NewGuid();
        var screenshotDir = Path.Combine(
            Path.GetTempPath(), "wwwroot", "uploads", "issues", issueId.ToString());
        Directory.CreateDirectory(screenshotDir);
        var screenshotFile = Path.Combine(screenshotDir, "shot.png");
        await File.WriteAllBytesAsync(screenshotFile, new byte[] { 1, 2, 3 });

        try
        {
            var reporterId = Guid.NewGuid();
            _dbContext.Users.Add(new User
            {
                Id = reporterId,
                Email = $"{reporterId}@test.com",
                DisplayName = "Reporter",
                PreferredLanguage = "en"
            });
            _dbContext.Issues.Add(new Issue
            {
                Id = issueId,
                ReporterUserId = reporterId,
                Category = IssueCategory.Bug,
                Title = "Has screenshot",
                Description = "...",
                Status = IssueStatus.Resolved,
                CreatedAt = oldResolved,
                UpdatedAt = oldResolved,
                ResolvedAt = oldResolved,
                ScreenshotStoragePath = $"uploads/issues/{issueId}/shot.png"
            });
            await _dbContext.SaveChangesAsync();

            var deleted = await _service.PurgeExpiredAsync();

            deleted.Should().Be(1);
            Directory.Exists(screenshotDir).Should().BeFalse();
        }
        finally
        {
            if (Directory.Exists(screenshotDir))
                Directory.Delete(screenshotDir, recursive: true);
        }
    }

    [HumansFact]
    public async Task PurgeExpiredAsync_TolerantOfMissingScreenshotDirectory()
    {
        // Issue claims a screenshot path but the directory was already removed
        // (e.g. wiped manually, prior partial sweep). The DB delete must still
        // succeed.
        var oldResolved = _clock.GetCurrentInstant() - Duration.FromDays(200);

        var reporterId = Guid.NewGuid();
        _dbContext.Users.Add(new User
        {
            Id = reporterId,
            Email = $"{reporterId}@test.com",
            DisplayName = "Reporter",
            PreferredLanguage = "en"
        });
        var issueId = Guid.NewGuid();
        _dbContext.Issues.Add(new Issue
        {
            Id = issueId,
            ReporterUserId = reporterId,
            Category = IssueCategory.Bug,
            Title = "Title",
            Description = "Description",
            Status = IssueStatus.Resolved,
            CreatedAt = oldResolved,
            UpdatedAt = oldResolved,
            ResolvedAt = oldResolved,
            ScreenshotStoragePath = $"uploads/issues/{issueId}/missing.png"
        });
        await _dbContext.SaveChangesAsync();

        var deleted = await _service.PurgeExpiredAsync();

        deleted.Should().Be(1);
        _dbContext.Issues.Any(i => i.Id == issueId).Should().BeFalse();
    }

    [HumansFact]
    public async Task PurgeExpiredAsync_InvalidatesNavBadge()
    {
        var oldResolved = _clock.GetCurrentInstant() - Duration.FromDays(200);
        await SeedIssueWithResolvedAtAsync(IssueStatus.Resolved, oldResolved);

        await _service.PurgeExpiredAsync();

        _navBadge.Received().Invalidate();
    }

    [HumansFact]
    public async Task PurgeExpiredAsync_NoOpWhenNothingExpired_DoesNotInvalidateNavBadge()
    {
        var deleted = await _service.PurgeExpiredAsync();

        deleted.Should().Be(0);
        _navBadge.DidNotReceive().Invalidate();
    }
}
