using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Options;
using Humans.Users.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Data.Repositories;
using Humans.Users.Services;

namespace Humans.Users.Tests.Services;

/// <summary>
/// The reconcile is the single OAuth-callback entry point that mutates <see cref="UserEmail"/>
/// rows, and it writes every audit row for the OAuth path.
/// </summary>
public class UserEmailServiceReconcileOAuthTests
{
    private readonly IUserRepository _repository = Substitute.For<IUserRepository>();
    private readonly IUserEmailRemovalListener _googleIdentity = Substitute.For<IUserEmailRemovalListener>();
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 11, 12, 0));
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly UserEmailService _service;

    private const string Provider = "Google";
    private const string ProviderKey = "sub-X";

    public UserEmailServiceReconcileOAuthTests()
    {
        var store = Substitute.For<IUserStore<User>>();
        var userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);
        _service = new UserEmailService(
            _repository,
            _userService,
            userManager,
            _clock,
            _auditLogService,
            [],
            [_googleIdentity],
            new EphemeralDataProtectionProvider(),
            TestUsersEmails.Create(),
            Options.Create(new EmailSettings { BaseUrl = TestUsersEmails.BaseUrl }),
            Substitute.For<IEmailService>(),
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
        await _repository.ApplyUserEmailReconcilePlanAsync(
            command.RowToDelete,
            command.RowToUpdate,
            command.RowToInsert,
            ct);

        return new UserEmailReconcilePlanResult(new HashSet<Guid> { userId });
    }

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
                IsVerified = true, IsPrimary = true, Provider = Provider, ProviderKey = ProviderKey,
            },
        };
        _repository.GetUserEmailsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(rows);

        var result = await _service.ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey,
            claimEmail: "Alice@Example.com", claimEmailVerified: true, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ReconcileOutcome.NoChange);
        result.AffectedRowId.Should().Be(rowId);
        await _repository.DidNotReceive().ApplyUserEmailReconcilePlanAsync(
            Arg.Any<UserEmail?>(), Arg.Any<UserEmail?>(), Arg.Any<UserEmail?>(),
            Arg.Any<CancellationToken>());
        await _auditLogService.DidNotReceive().LogAsync(
            Arg.Any<AuditActionDefinition>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

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
        _repository.GetUserEmailsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { tagged });

        var result = await _service.ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey,
            claimEmail: "new@example.com", claimEmailVerified: true, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ReconcileOutcome.EmailRewritten);
        result.AffectedRowId.Should().Be(rowId);
        result.PreviousEmail.Should().Be("old@example.com");
        tagged.Email.Should().Be("new@example.com");
        // The data change is applied atomically via ApplyUserEmailReconcilePlanAsync —
        // rewrite is a single row-to-update with no delete and no insert.
        await _repository.Received(1).ApplyUserEmailReconcilePlanAsync(
            rowToDelete: Arg.Is<UserEmail?>(r => r == null),
            rowToUpdate: Arg.Is<UserEmail?>(r => r != null && r.Id == rowId),
            rowToInsert: Arg.Is<UserEmail?>(r => r == null),
            Arg.Any<CancellationToken>());
        // The renamed-away address stops being the Google identity.
        await _googleIdentity.Received(1).AddressRemovedAsync(userId, "old@example.com", Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            UsersAuditActions.GoogleEmailRenamed,
            Arg.Any<string>(), userId,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansFact]
    public async Task TagMoved_TaggedRowExistsAndSiblingHoldsClaim_UnionsFlagsForceVerifyDeletesOld()
    {
        // Signing user has two rows:
        //   tagged row at old@example.com (Provider/ProviderKey set, IsPrimary=true, verified)
        //   sibling row at new@example.com (unverified, no flags)
        // Reconcile must move the tag onto the sibling, union IsPrimary
        // (sibling ends up IsPrimary=true), force IsVerified=true, delete the old
        // tagged row, and clear the old address as the Google identity.
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
        };
        _repository.GetUserEmailsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { oldRow, sibling });

        var result = await _service.ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey,
            claimEmail: "New@Example.com", claimEmailVerified: true, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ReconcileOutcome.TagMoved);
        result.AffectedRowId.Should().Be(siblingId);
        result.PreviousEmail.Should().Be("old@example.com");

        sibling.Provider.Should().Be(Provider);
        sibling.ProviderKey.Should().Be(ProviderKey);
        sibling.IsVerified.Should().BeTrue("OAuth callback proves ownership; tag-move force-verifies");
        sibling.IsPrimary.Should().BeTrue("flag-union: old row had IsPrimary=true");
        await _googleIdentity.Received(1).AddressRemovedAsync(userId, "old@example.com", Arg.Any<CancellationToken>());

        // Atomic plan: update the sibling AND delete the old tagged row in
        // one transaction.
        await _repository.Received(1).ApplyUserEmailReconcilePlanAsync(
            rowToDelete: Arg.Is<UserEmail?>(r => r != null && r.Id == oldRowId),
            rowToUpdate: Arg.Is<UserEmail?>(r => r != null && r.Id == siblingId),
            rowToInsert: Arg.Is<UserEmail?>(r => r == null),
            Arg.Any<CancellationToken>());
        await _auditLogService.Received(1).LogAsync(
            UsersAuditActions.GoogleEmailRenamed,
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
        };
        _repository.GetUserEmailsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { sibling });

        var result = await _service.ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey,
            claimEmail: "Match@Example.com", claimEmailVerified: true, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ReconcileOutcome.TagMoved);
        result.AffectedRowId.Should().Be(siblingId);
        sibling.Provider.Should().Be(Provider);
        sibling.ProviderKey.Should().Be(ProviderKey);
        sibling.IsVerified.Should().BeTrue();
        sibling.IsPrimary.Should().BeTrue("sibling kept its IsPrimary — no old row to flip it from");
        // Atomic plan: update the sibling, no delete (no old tagged row).
        await _repository.Received(1).ApplyUserEmailReconcilePlanAsync(
            rowToDelete: Arg.Is<UserEmail?>(r => r == null),
            rowToUpdate: Arg.Is<UserEmail?>(r => r != null && r.Id == siblingId),
            rowToInsert: Arg.Is<UserEmail?>(r => r == null),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NewRowCreated_NoTaggedRow_NoSibling_NoCrossUser_InsertsVerifiedTaggedRow()
    {
        var userId = Guid.NewGuid();
        _repository.GetUserEmailsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail>());

        UserEmail? planned = null;
        await _repository.ApplyUserEmailReconcilePlanAsync(
            Arg.Any<UserEmail?>(),
            Arg.Any<UserEmail?>(),
            Arg.Do<UserEmail?>(r => { if (r != null) planned = r; }),
            Arg.Any<CancellationToken>());

        var result = await _service.ReconcileOAuthIdentityAsync(
            userId, Provider, ProviderKey,
            claimEmail: "new@example.com", claimEmailVerified: true, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ReconcileOutcome.NewRowCreated);
        planned.Should().NotBeNull();
        planned!.UserId.Should().Be(userId);
        planned.Email.Should().Be("new@example.com");
        planned.Provider.Should().Be(Provider);
        planned.ProviderKey.Should().Be(ProviderKey);
        planned.IsVerified.Should().BeTrue();
        result.AffectedRowId.Should().Be(planned.Id);

        await _auditLogService.Received(1).LogAsync(
            UsersAuditActions.UserEmailLinked,
            Arg.Any<string>(), userId,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    [HumansTheory]
    [Xunit.InlineData(true)]
    [Xunit.InlineData(false)]
    public async Task CrossUserBlocked_OtherUserHoldsClaim_NoMutation_AuditedAttempt(bool claimEmailVerified)
    {
        var signingUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        var blockerRowId = Guid.NewGuid();
        var blocker = new UserEmail
        {
            Id = blockerRowId,
            UserId = otherUserId,
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
        _repository.GetUserEmailsByUserIdAsync(signingUserId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail> { tagged });
        _repository.GetUserEmailsByAddressAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns([blocker]);

        var result = await _service.ReconcileOAuthIdentityAsync(
            signingUserId, Provider, ProviderKey,
            claimEmail: "shared@example.com",
            claimEmailVerified: claimEmailVerified, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(ReconcileOutcome.CrossUserBlocked);
        result.BlockingUserId.Should().Be(otherUserId);
        result.BlockingRowId.Should().Be(blockerRowId);
        result.BlockingEmail.Should().Be("shared@example.com");

        // No mutation: signing user's tagged row keeps its old email; blocker
        // row is not removed; no new row is inserted; no atomic plan applied.
        tagged.Email.Should().Be("old@example.com");
        await _repository.DidNotReceive().ApplyUserEmailReconcilePlanAsync(
            Arg.Any<UserEmail?>(), Arg.Any<UserEmail?>(), Arg.Any<UserEmail?>(),
            Arg.Any<CancellationToken>());

        // Audited on the signing user only: nothing happened to the other account.
        await _auditLogService.Received(1).LogAsync(
            UsersAuditActions.OAuthRenameCollisionBlocked,
            Arg.Any<string>(), signingUserId,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
        await _auditLogService.DidNotReceive().LogAsync(
            UsersAuditActions.UserEmailDisplacedByOAuthRename,
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<Guid?>(), Arg.Any<string?>());
    }
}
