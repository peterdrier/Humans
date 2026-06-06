using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.Services.Users;

namespace Humans.Application.Tests.Services;

public class AccountMergeServiceMergeTests
{
    private readonly IAccountMergeRepository _mergeRepo = Substitute.For<IAccountMergeRepository>();
    private readonly IUserRepository _userEmailRepo = Substitute.For<IUserRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IUserInfoInvalidator _userInfoInvalidator = Substitute.For<IUserInfoInvalidator>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IActiveTeamsCacheInvalidator _activeTeamsCacheInvalidator = Substitute.For<IActiveTeamsCacheInvalidator>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly INotificationService _notify = Substitute.For<INotificationService>();
    private readonly IConsentCacheInvalidator _consentCache = Substitute.For<IConsentCacheInvalidator>();
    private readonly List<IUserMerge> _userMerges = [];
    private readonly FakeClock _clock = new(NodaTime.Instant.FromUtc(2026, 5, 5, 12, 0));

    public AccountMergeServiceMergeTests()
    {
        // MergeAsync's CloseRequestsForPairAsync filters GetPendingAsync in memory; default
        // it to empty so the happy path doesn't trip on a null result.
        _mergeRepo.GetPendingAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AccountMergeRequest>());
    }

    private AccountMergeService BuildSut() =>
        new(
            _mergeRepo, _userEmailRepo, _audit, _userInfoInvalidator,
            NullLogger<AccountMergeService>.Instance, _clock,
            _userMerges, _userService, _activeTeamsCacheInvalidator, _roles, _notify, _consentCache);

    private void SetupUsers(Guid sourceId, Guid targetId, bool sourceTombstoned = false)
    {
        // A real tombstone sets both MergedToUserId and MergedAt; IsMerged keys off MergedAt.
        _userService.GetUserInfoAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = sourceId,
                MergedToUserId = sourceTombstoned ? targetId : (Guid?)null,
                MergedAt = sourceTombstoned ? _clock.GetCurrentInstant() : (NodaTime.Instant?)null,
            }.ToUserInfo());
        _userService.GetUserInfoAsync(targetId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = targetId }.ToUserInfo());
    }

    [HumansFact]
    public async Task MergeAsync_HappyPath_RunsFanOutAndTombstone()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid(); var admin = Guid.NewGuid();
        SetupUsers(src, tgt);
        var merger = Substitute.For<IUserMerge>();
        _userMerges.Add(merger);

        // survivor = tgt, archived = src.
        await BuildSut().MergeAsync(tgt, src, admin);

        await merger.Received(1).ReassignAsync(src, tgt, admin,
            Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
        await _userService.Received(1).AnonymizeForMergeAsync(src, tgt,
            Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
        await _userEmailRepo.DidNotReceive().MarkUserEmailVerifiedAsync(
            Arg.Any<Guid>(), Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task MergeAsync_SurvivorEqualsArchived_Throws()
    {
        var id = Guid.NewGuid();
        var act = () => BuildSut().MergeAsync(id, id, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task MergeAsync_ArchivedMissing_Throws()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid();
        _userService.GetUserInfoAsync(tgt, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = tgt }, [], [], [], null, [], [], [], []));
        // archived (src) returns null by default — Substitute.For<>'s default for ValueTask<UserInfo?> is null
        var act = () => BuildSut().MergeAsync(tgt, src, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task MergeAsync_ArchivedAlreadyTombstoned_Throws()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid();
        SetupUsers(src, tgt, sourceTombstoned: true);
        var act = () => BuildSut().MergeAsync(tgt, src, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tombstoned*");
    }

    [HumansFact]
    public async Task ReconcileMergedRequestAsync_SetsAccepted_WhenPairAlreadyMerged()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid(); var admin = Guid.NewGuid();
        var req = new AccountMergeRequest
        {
            Id = Guid.NewGuid(),
            SourceUserId = src,
            TargetUserId = tgt,
            Email = "dupe@example.com",
            Status = AccountMergeRequestStatus.Pending,
        };
        _mergeRepo.GetByIdPlainAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        _mergeRepo.GetPendingAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AccountMergeRequest> { req });
        // Source already tombstoned, target survives.
        _userService.GetUserInfoAsync(src, Arg.Any<CancellationToken>())
            .Returns(new User { Id = src, MergedToUserId = tgt, MergedAt = _clock.GetCurrentInstant() }.ToUserInfo());
        _userService.GetUserInfoAsync(tgt, Arg.Any<CancellationToken>())
            .Returns(new User { Id = tgt }.ToUserInfo());

        await BuildSut().ReconcileMergedRequestAsync(req.Id, admin);

        req.Status.Should().Be(AccountMergeRequestStatus.Accepted);
        req.ResolvedByUserId.Should().Be(admin);
        await _mergeRepo.Received(1).UpdateAsync(
            Arg.Is<AccountMergeRequest>(r => r.Id == req.Id && r.Status == AccountMergeRequestStatus.Accepted),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileMergedRequestAsync_Throws_WhenNeitherAccountMerged()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid();
        var req = new AccountMergeRequest
        {
            Id = Guid.NewGuid(),
            SourceUserId = src,
            TargetUserId = tgt,
            Email = "dupe@example.com",
            Status = AccountMergeRequestStatus.Pending,
        };
        _mergeRepo.GetByIdPlainAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        _userService.GetUserInfoAsync(src, Arg.Any<CancellationToken>())
            .Returns(new User { Id = src }.ToUserInfo());
        _userService.GetUserInfoAsync(tgt, Arg.Any<CancellationToken>())
            .Returns(new User { Id = tgt }.ToUserInfo());

        var act = () => BuildSut().ReconcileMergedRequestAsync(req.Id, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not merged into each other*");
    }

    [HumansFact]
    public async Task ReconcileMergedRequestAsync_Throws_WhenOneAccountMergedIntoThirdParty()
    {
        // Codex P2: source merged into an UNRELATED third account, target still active.
        // The A<->B request must NOT be closeable — that conflict is still unresolved.
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid(); var third = Guid.NewGuid();
        var req = new AccountMergeRequest
        {
            Id = Guid.NewGuid(),
            SourceUserId = src,
            TargetUserId = tgt,
            Email = "dupe@example.com",
            Status = AccountMergeRequestStatus.Pending,
        };
        _mergeRepo.GetByIdPlainAsync(req.Id, Arg.Any<CancellationToken>()).Returns(req);
        _userService.GetUserInfoAsync(src, Arg.Any<CancellationToken>())
            .Returns(new User { Id = src, MergedToUserId = third, MergedAt = _clock.GetCurrentInstant() }.ToUserInfo());
        _userService.GetUserInfoAsync(tgt, Arg.Any<CancellationToken>())
            .Returns(new User { Id = tgt }.ToUserInfo());

        var act = () => BuildSut().ReconcileMergedRequestAsync(req.Id, Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not merged into each other*");
        await _mergeRepo.DidNotReceive().UpdateAsync(Arg.Any<AccountMergeRequest>(), Arg.Any<CancellationToken>());
    }
}
