using Humans.Auth.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Email.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.Notifications.Contracts;
using Humans.Settings.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Data;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Workgroups.Tests.Infrastructure;

/// <summary>
/// Base class for the section's service tests: a per-test in-memory
/// <see cref="WorkgroupsDbContext"/> and factory, a deterministic clock, substitutes for
/// every cross-section seam the inner <see cref="WorkgroupService"/> talks to, and seeders
/// for the six tables. Member names follow the other section harnesses
/// (memory/code/service-test-harness.md): <c>Db</c>, <c>DbFactory</c>, <c>Clock</c>, <c>SeedUser</c>.
/// </summary>
public abstract class WorkgroupsTestHarness : IDisposable
{
    private readonly Dictionary<Guid, UserInfo> _users = [];
    private bool _disposed;

    protected WorkgroupsTestHarness(Instant? now = null)
    {
        var options = new DbContextOptionsBuilder<WorkgroupsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options;
        Db = new WorkgroupsDbContext(options);
        DbFactory = new TestDbContextFactory<WorkgroupsDbContext>(options);
        Clock = new FakeClock(now ?? Instant.FromUtc(2026, 3, 1, 12, 0));

        Users = Substitute.For<IUserServiceRead>();
        Users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var id = call.Arg<Guid>();
                return new ValueTask<UserInfo?>(_users.GetValueOrDefault(id));
            });
        Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = call.Arg<IReadOnlyCollection<Guid>>();
                IReadOnlyDictionary<Guid, UserInfo> dict = ids
                    .Where(_users.ContainsKey)
                    .ToDictionary(id => id, id => _users[id]);
                return new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(dict);
            });
        Users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyCollection<UserInfo>>(_users.Values.ToList()));

        UserEmails = Substitute.For<IUserEmailService>();
        UserEmails.GetNotificationTargetEmailsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var ids = (IReadOnlyCollection<Guid>)call.Args()[0]!;
                IReadOnlyDictionary<Guid, string> dict = ids
                    .Where(_users.ContainsKey)
                    .ToDictionary(id => id, id => $"{id}@example.org");
                return Task.FromResult(dict);
            });

        Roles = Substitute.For<IRoleAssignmentService>();
        Roles.GetActiveUserIdsInRoleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns([]);

        Teams = Substitute.For<ITeamServiceRead>();
        Teams.GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((TeamInfo?)null);

        Settings = Substitute.For<ISettingsService>();
        RootFolderId = "root-folder";
        Settings.GetValueAsync(SettingKeys.WorkgroupsRootDriveFolderId, Arg.Any<CancellationToken>())
            .Returns(_ => RootFolderId);

        GoogleSync = Substitute.For<IGoogleSyncService>();
        GoogleSync.CreateSubfolderAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => $"folder-{Guid.NewGuid()}");

        Notifications = Substitute.For<INotificationService>();
        Email = Substitute.For<IEmailService>();
        EmailFactory = Substitute.For<IEmailMessageFactory>();
        EmailFactory.WorkgroupNotice(Arg.Any<WorkgroupNoticeRequest>())
            .Returns(call => new EmailMessage(
                call.Arg<WorkgroupNoticeRequest>().RecipientEmail,
                call.Arg<WorkgroupNoticeRequest>().RecipientName,
                "Workgroup notice", "body", "workgroup-notice"));

        AuditLog = Substitute.For<IAuditLogService>();
        Logger = new CapturingLogger<WorkgroupService>();
    }

    /// <summary>Null means "unset" — registration then throws RootFolderNotConfigured.</summary>
    private protected string? RootFolderId { get; set; }

    private protected WorkgroupsDbContext Db { get; }
    private protected TestDbContextFactory<WorkgroupsDbContext> DbFactory { get; }
    private protected FakeClock Clock { get; }
    private protected IUserServiceRead Users { get; }
    private protected IUserEmailService UserEmails { get; }
    private protected IRoleAssignmentService Roles { get; }
    private protected ITeamServiceRead Teams { get; }
    private protected ISettingsService Settings { get; }
    private protected IGoogleSyncService GoogleSync { get; }
    private protected INotificationService Notifications { get; }
    private protected IEmailService Email { get; }
    private protected IEmailMessageFactory EmailFactory { get; }
    private protected IAuditLogService AuditLog { get; }
    private protected CapturingLogger<WorkgroupService> Logger { get; }

    private protected static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    /// <summary>The undecorated service over the real repository and the substitutes above.</summary>
    private protected WorkgroupService NewService() => new(
        new WorkgroupRepository(DbFactory), Users, UserEmails, Roles, Settings, GoogleSync,
        Notifications, Email, EmailFactory, AuditLog, Clock, Logger);

    /// <summary>A fresh context over the same store — what a test reads back through.</summary>
    private protected WorkgroupsDbContext OpenContext() => DbFactory.CreateDbContext();

    // ── Seeders ───────────────────────────────────────────────────────────

    /// <summary>Registers a human the <see cref="IUserServiceRead"/> substitute knows by burner name.</summary>
    protected Guid SeedUser(
        string burnerName = "Test Human", Guid? id = null, ProfileInfo? profile = null)
    {
        var userId = id ?? Guid.NewGuid();
        _users[userId] = UserInfoFor(userId, burnerName, profile);
        return userId;
    }

    private protected async Task<Workgroup> SeedWorkgroupAsync(
        WorkgroupStatus status = WorkgroupStatus.Active,
        string name = "Finance 2026",
        Guid? coordinatorUserId = null,
        Instant? appliedAt = null,
        Instant? registeredAt = null,
        string? driveFolderId = "group-folder",
        Instant? dormantSince = null,
        WorkgroupDormantReason? dormantReason = null)
    {
        var now = Clock.GetCurrentInstant();
        var coordinator = coordinatorUserId ?? SeedUser("Coordinator");
        var workgroup = new Workgroup
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = Humans.Base.Helpers.SlugHelper.GenerateSlug(name),
            Purpose = "Purpose",
            Deliverable = "A report",
            DeliverableKind = WorkgroupDeliverableKind.Report,
            Audience = WorkgroupAudience.Board,
            Status = status,
            DormantReason = dormantReason,
            DriveFolderId = status is WorkgroupStatus.Active or WorkgroupStatus.Dormant ? driveFolderId : null,
            AppliedByUserId = coordinator,
            AppliedAt = appliedAt ?? now,
            RegisteredAt = status is WorkgroupStatus.Applied or WorkgroupStatus.Referred
                ? null
                : registeredAt ?? now,
            EndedAt = status is WorkgroupStatus.Dormant or WorkgroupStatus.Withdrawn or WorkgroupStatus.Refused
                ? now
                : null,
            DormantSince = dormantSince,
            CreatedAt = now,
            UpdatedAt = now
        };
        workgroup.Members.Add(new WorkgroupMember
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroup.Id,
            UserId = coordinator,
            Role = WorkgroupMemberRole.Coordinator,
            JoinedAt = now
        });

        Db.Workgroups.Add(workgroup);
        await Db.SaveChangesAsync(Ct);
        return workgroup;
    }

    private protected async Task AddMemberAsync(
        Guid workgroupId, Guid userId, WorkgroupMemberRole role = WorkgroupMemberRole.Member, Instant? joinedAt = null, Instant? leftAt = null)
    {
        Db.Members.Add(new WorkgroupMember
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            UserId = userId,
            Role = role,
            JoinedAt = joinedAt ?? Clock.GetCurrentInstant(),
            LeftAt = leftAt
        });
        await Db.SaveChangesAsync(Ct);
    }

    private protected async Task<WorkgroupLogEntry> AddLogEntryAsync(
        Guid workgroupId,
        WorkgroupLogKind kind,
        Instant? createdAt = null,
        Guid? authorUserId = null,
        LocalDate? occurredOn = null)
    {
        var now = createdAt ?? Clock.GetCurrentInstant();
        var entry = new WorkgroupLogEntry
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            Kind = kind,
            OccurredOn = occurredOn ?? now.InUtc().Date,
            AuthorUserId = authorUserId,
            CreatedAt = now,
            UpdatedAt = now
        };
        Db.LogEntries.Add(entry);
        await Db.SaveChangesAsync(Ct);
        return entry;
    }

    private protected async Task<WorkgroupMeeting> AddMeetingAsync(
        Guid workgroupId, Instant startUtc, bool isPublic = false, Instant? deletedAt = null)
    {
        var meeting = new WorkgroupMeeting
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            Title = "Meeting",
            StartUtc = startUtc,
            EndUtc = startUtc.Plus(Duration.FromHours(1)),
            IsPublic = isPublic,
            DeletedAt = deletedAt,
            CreatedAt = startUtc,
            UpdatedAt = startUtc
        };
        Db.Meetings.Add(meeting);
        await Db.SaveChangesAsync(Ct);
        return meeting;
    }

    private protected async Task<WorkgroupDocument> AddDocumentAsync(
        Guid workgroupId,
        WorkgroupDocumentStatus status = WorkgroupDocumentStatus.Draft,
        string body = "Body",
        IReadOnlyList<string>? categories = null,
        Instant? opensAt = null,
        Instant? closesAt = null,
        WorkgroupDisposition? disposition = null,
        Instant? deliveredAt = null)
    {
        var now = Clock.GetCurrentInstant();
        var document = new WorkgroupDocument
        {
            Id = Guid.NewGuid(),
            WorkgroupId = workgroupId,
            Title = "Document",
            Kind = WorkgroupDocumentKind.Deliverable,
            Body = body,
            Status = status,
            CommentCategories = categories?.ToList() ?? [],
            CommentsOpenAt = opensAt,
            CommentsCloseAt = closesAt,
            DeliveredAt = deliveredAt,
            Disposition = disposition,
            CreatedAt = now,
            UpdatedAt = now
        };
        Db.Documents.Add(document);
        await Db.SaveChangesAsync(Ct);
        return document;
    }

    private protected async Task<WorkgroupDocumentComment> AddCommentAsync(
        Guid documentId,
        string category = "Scope",
        Guid? authorUserId = null,
        WorkgroupCommentDisposition disposition = WorkgroupCommentDisposition.Pending,
        Instant? createdAt = null)
    {
        var comment = new WorkgroupDocumentComment
        {
            Id = Guid.NewGuid(),
            DocumentId = documentId,
            Category = category,
            AuthorUserId = authorUserId,
            Body = "Comment body",
            Disposition = disposition,
            CreatedAt = createdAt ?? Clock.GetCurrentInstant()
        };
        Db.Comments.Add(comment);
        await Db.SaveChangesAsync(Ct);
        return comment;
    }

    // ── UserInfo construction ────────────────────────────────────────────

    private static UserInfo UserInfoFor(Guid id, string burnerName, ProfileInfo? profile) => new(
        id, burnerName, false, "en", null, Instant.FromUtc(2026, 1, 1, 0, 0),
        null, null, null, null, null, false, null, false, null, null, null,
        null, null, null, [], [], [], profile, []);

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        if (disposing) Db.Dispose();
        _disposed = true;
    }
}
