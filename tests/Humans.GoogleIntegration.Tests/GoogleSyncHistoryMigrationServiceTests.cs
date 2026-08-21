using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// Behaviour of the one-time <c>audit_log</c> → <c>google_sync_log</c> history move
/// (nobodies-collective/Humans#1083): what maps, what does not, and that a second run is a
/// no-op. Runs against a real <see cref="GoogleSyncLogRepository"/> so the idempotency claim
/// is tested through EF rather than through a substitute's memory.
/// </summary>
public sealed class GoogleSyncHistoryMigrationServiceTests : IDisposable
{
    private static readonly Instant Noon = Instant.FromUtc(2026, 5, 4, 12, 0);

    private readonly GoogleIntegrationDbContext _seedContext;
    private readonly ILegacyGoogleSyncAuditReader _legacyAudit = Substitute.For<ILegacyGoogleSyncAuditReader>();
    private readonly GoogleSyncHistoryMigrationService _service;

    public GoogleSyncHistoryMigrationServiceTests()
    {
        var options = new DbContextOptionsBuilder<GoogleIntegrationDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _seedContext = new GoogleIntegrationDbContext(options);

        var teamResources = Substitute.For<ITeamResourceService>();
        teamResources.GetResourceNamesByIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());

        _service = new GoogleSyncHistoryMigrationService(
            _legacyAudit,
            new GoogleSyncLogRepository(new SingleContextFactory(options)),
            teamResources,
            NullLogger<GoogleSyncHistoryMigrationService>.Instance);
    }

    public void Dispose() => _seedContext.Dispose();

    [HumansFact]
    public async Task MigrateAsync_CopiesEachAuditFieldOntoTheSyncLogRow()
    {
        var userId = Guid.NewGuid();
        var resourceId = Guid.NewGuid();
        var audit = Granted(resourceId) with
        {
            RelatedEntityId = userId,
            RelatedEntityType = "User",
            Description = "GoogleGroupSyncService: Granted Google Group access to a@b.org (Docs)",
            Role = "MEMBER",
            SyncSource = GoogleSyncSource.ScheduledSync,
            UserEmail = "a@b.org"
        };
        Legacy(audit);

        var report = await _service.MigrateAsync(Ct);

        report.Moved.Should().Be(1);
        var row = await _seedContext.GoogleSyncLog.AsNoTracking().SingleAsync(Ct);
        // The audit row's own id is what makes a re-run a no-op.
        row.Id.Should().Be(audit.Id);
        row.Action.Should().Be(GoogleSyncLogAction.AccessGranted);
        row.ResourceId.Should().Be(resourceId);
        row.UserId.Should().Be(userId);
        row.UserEmail.Should().Be("a@b.org");
        row.Role.Should().Be("MEMBER");
        row.Source.Should().Be(GoogleSyncSource.ScheduledSync);
        row.Success.Should().BeTrue();
        row.OccurredAt.Should().Be(audit.OccurredAt);
        row.Description.Should().Be(audit.Description);
        // JobName was folded into "JobName: description" by the retired writer.
        row.JobName.Should().Be("GoogleGroupSyncService");
    }

    [HumansFact]
    public async Task MigrateAsync_CarriesTheFailureDetailOfARevocation()
    {
        var audit = Granted(Guid.NewGuid()) with
        {
            Action = AuditAction.GoogleResourceAccessRevoked,
            Success = false,
            ErrorMessage = "Google group remove failed for a@b.org"
        };
        Legacy(audit);

        await _service.MigrateAsync(Ct);

        var row = await _seedContext.GoogleSyncLog.AsNoTracking().SingleAsync(Ct);
        row.Action.Should().Be(GoogleSyncLogAction.AccessRevoked);
        row.Success.Should().BeFalse();
        row.ErrorMessage.Should().Be("Google group remove failed for a@b.org");
    }

    [HumansFact]
    public async Task MigrateAsync_LeavesAnAlreadyMovedRowAlone()
    {
        var stays = Granted(Guid.NewGuid());
        var moves = Granted(Guid.NewGuid());
        Legacy(stays, moves);

        var first = await _service.MigrateAsync(Ct);
        var second = await _service.MigrateAsync(Ct);

        first.Moved.Should().Be(2);
        first.AlreadyPresent.Should().Be(0);

        second.Examined.Should().Be(2);
        second.AlreadyPresent.Should().Be(2);
        second.Movable.Should().Be(0);
        second.Moved.Should().Be(0);
        (await _seedContext.GoogleSyncLog.AsNoTracking().CountAsync(Ct)).Should().Be(2);
    }

    [HumansFact]
    public async Task MigrateAsync_MovesOnlyTheRowsThatArentThereYet()
    {
        var alreadyMoved = Granted(Guid.NewGuid());
        Legacy(alreadyMoved);
        await _service.MigrateAsync(Ct);

        Legacy(alreadyMoved, Granted(Guid.NewGuid()), Granted(Guid.NewGuid()));
        var report = await _service.MigrateAsync(Ct);

        report.Examined.Should().Be(3);
        report.AlreadyPresent.Should().Be(1);
        report.Moved.Should().Be(2);
        (await _seedContext.GoogleSyncLog.AsNoTracking().CountAsync(Ct)).Should().Be(3);
    }

    [HumansFact]
    public async Task MigrateAsync_SkipsRowsWithNoSyncLogEquivalent()
    {
        var otherAction = Granted(Guid.NewGuid()) with { Action = AuditAction.GoogleResourceProvisioned };
        var noPayload = Granted(Guid.NewGuid()) with { SyncSource = null, Success = null };
        Legacy(otherAction, noPayload, Granted(Guid.NewGuid()));

        var report = await _service.MigrateAsync(Ct);

        report.Examined.Should().Be(3);
        report.Movable.Should().Be(1);
        report.Skipped.Should().Be(2);
        report.SkippedRows.Should().HaveCount(2);
        report.SkippedRows.Should().Contain(r =>
            r.AuditId == otherAction.Id && r.Reason.Contains("GoogleResourceProvisioned"));
        report.SkippedRows.Should().Contain(r =>
            r.AuditId == noPayload.Id && r.Reason.Contains("SyncSource"));
        (await _seedContext.GoogleSyncLog.AsNoTracking().CountAsync(Ct)).Should().Be(1);
    }

    [HumansFact]
    public async Task PreviewAsync_CountsWithoutWriting()
    {
        Legacy(Granted(Guid.NewGuid()), Granted(Guid.NewGuid()));

        var report = await _service.PreviewAsync(Ct);

        report.Examined.Should().Be(2);
        report.Movable.Should().Be(2);
        report.Moved.Should().Be(0);
        report.MovableRows.Should().HaveCount(2);
        (await _seedContext.GoogleSyncLog.AsNoTracking().AnyAsync(Ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task PreviewAsync_ReportsNothingWhenTheAuditLogHasNoGoogleRows()
    {
        Legacy();

        var report = await _service.PreviewAsync(Ct);

        report.Examined.Should().Be(0);
        report.Movable.Should().Be(0);
        report.Skipped.Should().Be(0);
    }

    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private void Legacy(params LegacyGoogleSyncAuditRow[] rows) =>
        _legacyAudit.GetLegacyGoogleSyncRowsAsync(Arg.Any<CancellationToken>()).Returns(rows);

    private static LegacyGoogleSyncAuditRow Granted(Guid resourceId) => new(
        Id: Guid.NewGuid(),
        Action: AuditAction.GoogleResourceAccessGranted,
        OccurredAt: Noon,
        Description: "GoogleWorkspaceSyncService: Granted Drive access",
        ResourceId: resourceId,
        RelatedEntityId: null,
        RelatedEntityType: null,
        UserEmail: "a@b.org",
        Role: "writer",
        SyncSource: GoogleSyncSource.ManualSync,
        Success: true,
        ErrorMessage: null);

    private sealed class SingleContextFactory(DbContextOptions<GoogleIntegrationDbContext> options)
        : IDbContextFactory<GoogleIntegrationDbContext>
    {
        public GoogleIntegrationDbContext CreateDbContext() => new(options);

        public Task<GoogleIntegrationDbContext> CreateDbContextAsync(CancellationToken ct = default) =>
            Task.FromResult(new GoogleIntegrationDbContext(options));
    }
}
