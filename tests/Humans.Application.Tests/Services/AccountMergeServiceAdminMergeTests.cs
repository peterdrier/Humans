using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public class AccountMergeServiceAdminMergeTests
{
    private readonly IAccountMergeRepository _mergeRepo = Substitute.For<IAccountMergeRepository>();
    private readonly IUserEmailRepository _userEmailRepo = Substitute.For<IUserEmailRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IUserInfoInvalidator _userInfoInvalidator = Substitute.For<IUserInfoInvalidator>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IActiveTeamsCacheInvalidator _activeTeamsCacheInvalidator = Substitute.For<IActiveTeamsCacheInvalidator>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly INotificationService _notify = Substitute.For<INotificationService>();
    private readonly IConsentCacheInvalidator _consentCache = Substitute.For<IConsentCacheInvalidator>();
    private readonly List<IUserMerge> _userMerges = [];
    private readonly FakeClock _clock = new(NodaTime.Instant.FromUtc(2026, 5, 5, 12, 0));

    private AccountMergeService BuildSut() =>
        new(
            _mergeRepo, _userEmailRepo, _audit, _userInfoInvalidator,
            NullLogger<AccountMergeService>.Instance, _clock,
            _userMerges, _userService, _activeTeamsCacheInvalidator, _roles, _notify, _consentCache);

    private void SetupUsers(Guid sourceId, Guid targetId, bool sourceTombstoned = false)
    {
        _userService.GetUserInfoAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = sourceId, MergedToUserId = sourceTombstoned ? targetId : (Guid?)null }.ToUserInfo());
        _userService.GetUserInfoAsync(targetId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = targetId }.ToUserInfo());
    }

    [HumansFact]
    public async Task AdminMergeAsync_HappyPath_RunsFanOutAndTombstone()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid(); var admin = Guid.NewGuid();
        SetupUsers(src, tgt);
        var merger = Substitute.For<IUserMerge>();
        _userMerges.Add(merger);

        await BuildSut().AdminMergeAsync(src, tgt, admin);

        await merger.Received(1).ReassignAsync(src, tgt, admin,
            Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
        await _userService.Received(1).AnonymizeForMergeAsync(src, tgt,
            Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
        await _userEmailRepo.DidNotReceive().MarkVerifiedAsync(
            Arg.Any<Guid>(), Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdminMergeAsync_SourceEqualsTarget_Throws()
    {
        var id = Guid.NewGuid();
        var act = () => BuildSut().AdminMergeAsync(id, id, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task AdminMergeAsync_SourceMissing_Throws()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid();
        _userService.GetUserInfoAsync(tgt, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = tgt }, [], [], [], null, [], [], [], []));
        // source returns null by default — Substitute.For<>'s default for ValueTask<UserInfo?> is null
        var act = () => BuildSut().AdminMergeAsync(src, tgt, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [HumansFact]
    public async Task AdminMergeAsync_SourceAlreadyTombstoned_Throws()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid();
        SetupUsers(src, tgt, sourceTombstoned: true);
        var act = () => BuildSut().AdminMergeAsync(src, tgt, Guid.NewGuid());
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*tombstoned*");
    }
}
