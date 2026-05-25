using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

/// <summary>
/// Tests for <see cref="IUserEmailService.ReconcileOAuthIdentityAsync"/>
/// (issue nobodies-collective/Humans#697). The reconcile method is the
/// single OAuth-callback entry point that mutates <see cref="UserEmail"/>
/// rows; it owns every audit row written for the OAuth path.
/// </summary>
public class UserEmailServiceReconcileOAuthTests
{
    private readonly IUserEmailRepository _repository = Substitute.For<IUserEmailRepository>();
    private readonly IAccountMergeService _mergeService = Substitute.For<IAccountMergeService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly UserManager<User> _userManager;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 11, 12, 0));
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IServiceProvider _serviceProvider;
    private readonly UserEmailService _service;

    private const string Provider = "Google";
    private const string ProviderKey = "sub-X";

    public UserEmailServiceReconcileOAuthTests()
    {
        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);
        _serviceProvider = new ServiceLocatorBuilder().With(_mergeService).Build();

        _service = new UserEmailService(
            _repository,
            _userService,
            _userManager,
            _clock,
            _auditLogService,
            _serviceProvider,
            NullLogger<UserEmailService>.Instance);

        _userService.ApplyUserEmailReconcilePlanAsync(
                Arg.Any<Guid>(),
                Arg.Any<UserEmailReconcilePlanCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(call => ApplyReconcilePlanThroughRepositoryAsync(
                call.ArgAt<Guid>(0),
                call.ArgAt<UserEmailReconcilePlanCommand>(1),
                call.ArgAt<CancellationToken>(2)));
    }

    private async Task<UserEmailReconcilePlanResult> ApplyReconcilePlanThroughRepositoryAsync(
        Guid userId,
        UserEmailReconcilePlanCommand command,
        CancellationToken ct)
    {
        await _repository.ApplyReconcilePlanAsync(
            command.DisplacedRowToDelete,
            command.RowToDelete,
            command.RowToUpdate,
            command.RowToInsert,
            ct);

        var mutatedUserIds = new HashSet<Guid> { userId };
        if (command.DisplacedRowToDelete is not null)
            mutatedUserIds.Add(command.DisplacedRowToDelete.UserId);

        return new UserEmailReconcilePlanResult(mutatedUserIds);
    }

    // ─── NoChange ────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task NoChange_TaggedRowAtClaimEmail_NoMutation_NoAudit()
    {
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var rows = new List<UserEmail>
        {
            new()
            {
                Id = rowId, UserId = userId, Email = "alice@example.com",
                IsVerified = true, IsPrimary = true, IsGoogle = true,
                Provider = Provider, ProviderKey = ProviderKey,
            },
        };
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(rows);

        var result = await _service.ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey,
            claimEmail: "Alice@Example.com", claimEmailVerified: true);

        result.Outcome.Should().Be(ReconcileOutcome.NoChange);
        result.AffectedRowId.Should().Be(rowId);
        await _repository.DidNotReceive().ApplyReconcilePlanAsync(
            Arg.Any<UserEmail?>(), Arg.Any<UserEmail?>(),
            Arg.Any<UserEmail?>(), Arg.Any<UserEmail?>(),
            Arg.Any<CancellationToken>());
        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ─── EmailRewritten ──────────────────────────────────────────────────────

    [HumansFact]
    public async Task EmailRewritten_TaggedRowAtDifferentEmail_NoSiblingHoldsClaim_RewritesInPlace()
    {
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        var tagged = new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "old@example.com",
            IsVerified = true,
            IsPrimary = true,
            Provider = Provider,
            ProviderKey = ProviderKey,
        };
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { tagged });

        var result = await _service.ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey,
            claimEmail: "new@example.com", claimEmailVerified: true);

        result.Outcome.Should().Be(ReconcileOutcome.EmailRewritten);
        result.AffectedRowId.Should().Be(rowId);
        result.PreviousEmail.Should().Be("old@example.com");
        tagged.Email.Should().Be("new@example.com");
        // The data change is applied atomically via ApplyReconcilePlanAsync —
        // rewrite is a single row-to-update with no delete and no insert.
        await _repository.Received(1).ApplyReconcilePlanAsync(
            displacedRowToDelete: Arg.Is<UserEmail?>(r => r == null),
            rowToDelete: Arg.Is<UserEmail?>(r => r == null),
            rowToUpdate: Arg.Is<UserEmail?>(r => r != null && r.Id == rowId),
            rowToInsert: Arg.Is<UserEmail?>(r => r == null),
            Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.GoogleEmailRenamed,
            Arg.Any<string>(), userId,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ─── TagMoved (tagged-row + sibling-at-claim) ────────────────────────────

    [HumansFact]
    public async Task TagMoved_TaggedRowExistsAndSiblingHoldsClaim_UnionsFlagsForceVerifyDeletesOld()
    {
        // Signing user has two rows:
        //   tagged row at old@example.com (Provider/ProviderKey set, IsPrimary=true, IsGoogle=true, verified)
        //   sibling row at new@example.com (unverified, no flags)
        // Reconcile must move the tag onto the sibling, union the flags
        // (sibling ends up IsPrimary=true + IsGoogle=true), force IsVerified=true,
        // and delete the old tagged row.
        var userId = Guid.NewGuid();
        var oldRowId = Guid.NewGuid();
        var siblingId = Guid.NewGuid();
        var oldRow = new UserEmail
        {
            Id = oldRowId,
            UserId = userId,
            Email = "old@example.com",
            IsVerified = true,
            IsPrimary = true,
            IsGoogle = true,
            Provider = Provider,
            ProviderKey = ProviderKey,
        };
        var sibling = new UserEmail
        {
            Id = siblingId,
            UserId = userId,
            Email = "new@example.com",
            IsVerified = false,
            IsPrimary = false,
            IsGoogle = false,
        };
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { oldRow, sibling });

        var result = await _service.ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey,
            claimEmail: "New@Example.com", claimEmailVerified: true);

        result.Outcome.Should().Be(ReconcileOutcome.TagMoved);
        result.AffectedRowId.Should().Be(siblingId);
        result.PreviousEmail.Should().Be("old@example.com");

        sibling.Provider.Should().Be(Provider);
        sibling.ProviderKey.Should().Be(ProviderKey);
        sibling.IsVerified.Should().BeTrue("OAuth callback proves ownership; tag-move force-verifies");
        sibling.IsPrimary.Should().BeTrue("flag-union: old row had IsPrimary=true");
        sibling.IsGoogle.Should().BeTrue("flag-union: old row had IsGoogle=true");

        // Atomic plan: update the sibling AND delete the old tagged row in
        // one transaction.
        await _repository.Received(1).ApplyReconcilePlanAsync(
            displacedRowToDelete: Arg.Is<UserEmail?>(r => r == null),
            rowToDelete: Arg.Is<UserEmail?>(r => r != null && r.Id == oldRowId),
            rowToUpdate: Arg.Is<UserEmail?>(r => r != null && r.Id == siblingId),
            rowToInsert: Arg.Is<UserEmail?>(r => r == null),
            Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.GoogleEmailRenamed,
            Arg.Any<string>(), userId,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task TagMoved_NoExistingTaggedRowButSiblingHoldsClaim_AttachesTagToSibling()
    {
        // Signing user has one row at the claim email but no tagged row yet
        // (legacy backfill case). Reconcile attaches the tag onto the existing
        // row. Flags on the sibling carry through unchanged — there is no
        // old row to union from.
        var userId = Guid.NewGuid();
        var siblingId = Guid.NewGuid();
        var sibling = new UserEmail
        {
            Id = siblingId,
            UserId = userId,
            Email = "match@example.com",
            IsVerified = true,
            IsPrimary = true,
            IsGoogle = true,
        };
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { sibling });

        var result = await _service.ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey,
            claimEmail: "Match@Example.com", claimEmailVerified: true);

        result.Outcome.Should().Be(ReconcileOutcome.TagMoved);
        result.AffectedRowId.Should().Be(siblingId);
        sibling.Provider.Should().Be(Provider);
        sibling.ProviderKey.Should().Be(ProviderKey);
        sibling.IsVerified.Should().BeTrue();
        sibling.IsPrimary.Should().BeTrue("sibling kept its IsPrimary — no old row to flip it from");
        sibling.IsGoogle.Should().BeTrue("sibling kept its IsGoogle — no old row to flip it from");
        // Atomic plan: update the sibling, no delete (no old tagged row).
        await _repository.Received(1).ApplyReconcilePlanAsync(
            displacedRowToDelete: Arg.Is<UserEmail?>(r => r == null),
            rowToDelete: Arg.Is<UserEmail?>(r => r == null),
            rowToUpdate: Arg.Is<UserEmail?>(r => r != null && r.Id == siblingId),
            rowToInsert: Arg.Is<UserEmail?>(r => r == null),
            Arg.Any<CancellationToken>());
    }

    // ─── NewRowCreated ───────────────────────────────────────────────────────

    [HumansFact]
    public async Task NewRowCreated_NoTaggedRow_NoSibling_NoCrossUser_InsertsVerifiedTaggedRow()
    {
        var userId = Guid.NewGuid();
        _repository.GetByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail>());

        UserEmail? planned = null;
        await _repository.ApplyReconcilePlanAsync(
            Arg.Any<UserEmail?>(),
            Arg.Any<UserEmail?>(),
            Arg.Any<UserEmail?>(),
            Arg.Do<UserEmail?>(r => { if (r != null) planned = r; }),
            Arg.Any<CancellationToken>());

        var result = await _service.ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey,
            claimEmail: "new@example.com", claimEmailVerified: true);

        result.Outcome.Should().Be(ReconcileOutcome.NewRowCreated);
        planned.Should().NotBeNull();
        planned!.UserId.Should().Be(userId);
        planned.Email.Should().Be("new@example.com");
        planned.Provider.Should().Be(Provider);
        planned.ProviderKey.Should().Be(ProviderKey);
        planned.IsVerified.Should().BeTrue();
        result.AffectedRowId.Should().Be(planned.Id);

        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailLinked,
            Arg.Any<string>(), userId,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ─── CrossUserDisplaced ──────────────────────────────────────────────────

    [HumansFact]
    public async Task CrossUserDisplaced_ClaimVerified_OtherUserHoldsClaim_DeletesOtherRowProceedsAndAudits()
    {
        var signingUserId = Guid.NewGuid();
        var displacedUserId = Guid.NewGuid();
        var displacedRowId = Guid.NewGuid();
        var displacedRow = new UserEmail
        {
            Id = displacedRowId,
            UserId = displacedUserId,
            Email = "shared@example.com",
            IsVerified = true,
            IsPrimary = true,
        };
        // Signing user has no rows at all → reconcile would NewRowCreate, but
        // cross-user check fires first.
        _repository.GetByUserIdForMutationAsync(signingUserId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail>());
        // Displaced user retains another verified row after the delete, so the
        // "left without verified email" flag stays false.
        var displacedSurvivor = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = displacedUserId,
            Email = "survivor@example.com",
            IsVerified = true,
            IsPrimary = false,
        };
        _repository.GetByUserIdForMutationAsync(displacedUserId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { displacedRow, displacedSurvivor });
        _repository.FindOtherUsersVerifiedRowAsync(
                Arg.Any<string>(), Arg.Any<string?>(), signingUserId, Arg.Any<CancellationToken>())
            .Returns(displacedRow);

        var result = await _service.ReconcileOAuthIdentityAsync(
            signingUserId, Provider, ProviderKey,
            claimEmail: "shared@example.com", claimEmailVerified: true);

        result.Outcome.Should().Be(ReconcileOutcome.CrossUserDisplaced);
        result.DisplacedUserId.Should().Be(displacedUserId);
        result.DisplacedRowId.Should().Be(displacedRowId);
        result.DisplacedEmail.Should().Be("shared@example.com");
        result.DisplacedUserLeftWithoutVerifiedEmail.Should().BeFalse();

        // Atomic plan: the displaced row is deleted AND the signing user's
        // NewRowCreated insert happens in the same transaction.
        await _repository.Received(1).ApplyReconcilePlanAsync(
            displacedRowToDelete: Arg.Is<UserEmail?>(r => r != null && r.Id == displacedRowId),
            rowToDelete: Arg.Is<UserEmail?>(r => r == null),
            rowToUpdate: Arg.Is<UserEmail?>(r => r == null),
            rowToInsert: Arg.Is<UserEmail?>(r => r != null && r.UserId == signingUserId),
            Arg.Any<CancellationToken>());
        // Paired audit rows: collision on signing user + displacement on displaced user.
        await _auditLogService.Received(1).LogAsync(
            AuditAction.OAuthRenameCollision,
            Arg.Any<string>(), signingUserId,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailDisplacedByOAuthRename,
            Arg.Any<string>(), displacedUserId,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task CrossUserDisplaced_DisplacedUserLeftWithoutVerifiedEmail_SetsFlagAndDescribesIt()
    {
        var signingUserId = Guid.NewGuid();
        var displacedUserId = Guid.NewGuid();
        var displacedRowId = Guid.NewGuid();
        var displacedRow = new UserEmail
        {
            Id = displacedRowId,
            UserId = displacedUserId,
            Email = "shared@example.com",
            IsVerified = true,
            IsPrimary = true,
        };
        _repository.GetByUserIdForMutationAsync(signingUserId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail>());
        // Displaced user has ONLY the row being displaced — after the delete
        // they're left with zero verified emails.
        _repository.GetByUserIdForMutationAsync(displacedUserId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { displacedRow });
        _repository.FindOtherUsersVerifiedRowAsync(
                Arg.Any<string>(), Arg.Any<string?>(), signingUserId, Arg.Any<CancellationToken>())
            .Returns(displacedRow);

        var result = await _service.ReconcileOAuthIdentityAsync(
            signingUserId, Provider, ProviderKey,
            claimEmail: "shared@example.com", claimEmailVerified: true);

        result.Outcome.Should().Be(ReconcileOutcome.CrossUserDisplaced);
        result.DisplacedUserLeftWithoutVerifiedEmail.Should().BeTrue();
        await _auditLogService.Received(1).LogAsync(
            AuditAction.UserEmailDisplacedByOAuthRename,
            Arg.Any<string>(), displacedUserId,
            // Audit description must explicitly state the empty-verified state.
            Arg.Is<string>(s => s.Contains("zero verified", StringComparison.OrdinalIgnoreCase)
                || s.Contains("without verified", StringComparison.OrdinalIgnoreCase)),
            Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    // ─── CrossUserBlocked ────────────────────────────────────────────────────

    [HumansFact]
    public async Task CrossUserBlocked_ClaimNotVerified_OtherUserHoldsClaim_NoMutation_AuditedAttempt()
    {
        var signingUserId = Guid.NewGuid();
        var displacedUserId = Guid.NewGuid();
        var blockerRowId = Guid.NewGuid();
        var blocker = new UserEmail
        {
            Id = blockerRowId,
            UserId = displacedUserId,
            Email = "shared@example.com",
            IsVerified = true,
            IsPrimary = true,
        };
        // Signing user already has a tagged row at a different email — that row
        // MUST remain at its original email; no rewrite, no insert.
        var taggedId = Guid.NewGuid();
        var tagged = new UserEmail
        {
            Id = taggedId,
            UserId = signingUserId,
            Email = "old@example.com",
            IsVerified = true,
            IsPrimary = true,
            Provider = Provider,
            ProviderKey = ProviderKey,
        };
        _repository.GetByUserIdForMutationAsync(signingUserId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { tagged });
        _repository.FindOtherUsersVerifiedRowAsync(
                Arg.Any<string>(), Arg.Any<string?>(), signingUserId, Arg.Any<CancellationToken>())
            .Returns(blocker);

        var result = await _service.ReconcileOAuthIdentityAsync(
            signingUserId, Provider, ProviderKey,
            claimEmail: "shared@example.com",
            claimEmailVerified: false);

        result.Outcome.Should().Be(ReconcileOutcome.CrossUserBlocked);
        result.DisplacedUserId.Should().Be(displacedUserId);
        result.DisplacedRowId.Should().Be(blockerRowId);
        result.DisplacedEmail.Should().Be("shared@example.com");

        // No mutation: signing user's tagged row keeps its old email; blocker
        // row is not removed; no new row is inserted; no atomic plan applied.
        tagged.Email.Should().Be("old@example.com");
        await _repository.DidNotReceive().ApplyReconcilePlanAsync(
            Arg.Any<UserEmail?>(), Arg.Any<UserEmail?>(),
            Arg.Any<UserEmail?>(), Arg.Any<UserEmail?>(),
            Arg.Any<CancellationToken>());

        // The blocked attempt is audited on the signing user only — no audit
        // on the displaced user since nothing happened to them.
        await _auditLogService.Received(1).LogAsync(
            AuditAction.OAuthRenameCollisionBlocked,
            Arg.Any<string>(), signingUserId,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _auditLogService.DidNotReceive().LogAsync(
            AuditAction.UserEmailDisplacedByOAuthRename,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }
}
