using AwesomeAssertions;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
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
    private readonly IFullProfileInvalidator _fullProfileInvalidator = Substitute.For<IFullProfileInvalidator>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ITeamService _team = Substitute.For<ITeamService>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly INotificationService _notify = Substitute.For<INotificationService>();
    private readonly List<IUserMerge> _userMerges = new();
    private readonly FakeClock _clock = new(NodaTime.Instant.FromUtc(2026, 5, 5, 12, 0));

    private AccountMergeService BuildSut() =>
        new(
            _mergeRepo, _userEmailRepo, _audit, _fullProfileInvalidator,
            NullLogger<AccountMergeService>.Instance, _clock,
            _userMerges, _userService, _team, _roles, _notify);

    private void SetupUsers(Guid sourceId, Guid targetId, bool sourceTombstoned = false)
    {
        _userService.GetByIdAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = sourceId, MergedToUserId = sourceTombstoned ? targetId : (Guid?)null });
        _userService.GetByIdAsync(targetId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = targetId });
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
        _userService.GetByIdAsync(tgt, Arg.Any<CancellationToken>())
            .Returns(new User { Id = tgt });
        // source returns null by default — Substitute.For<>'s default for Task<User?> is null
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
