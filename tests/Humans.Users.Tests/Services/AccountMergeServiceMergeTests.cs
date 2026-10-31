using Xunit;
using Microsoft.EntityFrameworkCore;
using Humans.Users.Data;
using AwesomeAssertions;
using Humans.Users.Tests.Infrastructure;
using Humans.AuditLog.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Services;
using Humans.Users.Contracts;
using Humans.Users.Data.Repositories;

namespace Humans.Users.Tests.Services;

public class AccountMergeServiceMergeTests
{
    private readonly IAccountMergeRepository _mergeRepo = Substitute.For<IAccountMergeRepository>();
    private readonly IUserRepository _userEmailRepo = Substitute.For<IUserRepository>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IUserInfoSliceRefresher _userInfoRefresher = Substitute.For<IUserInfoSliceRefresher>();
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly List<IUserMerge> _userMerges = [];
    private readonly FakeClock _clock = new(NodaTime.Instant.FromUtc(2026, 5, 5, 12, 0));

    public AccountMergeServiceMergeTests()
    {
        // MergeAsync's CloseRequestsForPairAsync filters GetPendingAsync in memory; default
        // it to empty so the happy path doesn't trip on a null result.
        _mergeRepo.GetPendingAsync(Arg.Any<CancellationToken>())
            .Returns(new List<AccountMergeRequest>());
    }

    private AccountMergeService BuildSut(IAccountMergeRepository? mergeRepository = null) =>
        new(
            mergeRepository ?? _mergeRepo, _userEmailRepo, _audit, _userInfoRefresher,
            NullLogger<AccountMergeService>.Instance, _clock,
            _userMerges, _userService);

    private void SetupUsers(Guid sourceId, Guid targetId, bool sourceTombstoned = false)
    {
        // A real tombstone sets both MergedToUserId and MergedAt; IsMerged keys off MergedAt.
        _userService.GetRawUserInfoAsync(sourceId, Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = sourceId,
                MergedToUserId = sourceTombstoned ? targetId : null,
                MergedAt = sourceTombstoned ? _clock.GetCurrentInstant() : null,
            }.ToUserInfo());
        _userService.GetRawUserInfoAsync(targetId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = targetId }.ToUserInfo());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContributeForUserAsync_IncludesEveryMergedAccountsRequestsOnce(bool requestArchivedId)
    {
        var oldestId = Guid.NewGuid();
        var archivedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var outsiderId = Guid.NewGuid();
        var unrelatedSourceId = Guid.NewGuid();
        var unrelatedTargetId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new UsersDbContext(options);
        db.Users.AddRange(new[] { oldestId, archivedId, survivorId, outsiderId, unrelatedSourceId, unrelatedTargetId }
            .Select(id => new User { Id = id }));
        foreach (var (source, target, status) in new[]
                 {
                     (oldestId, archivedId, AccountMergeRequestStatus.Accepted),
                     (archivedId, survivorId, AccountMergeRequestStatus.Accepted),
                     (survivorId, outsiderId, AccountMergeRequestStatus.Pending),
                     (unrelatedSourceId, unrelatedTargetId, AccountMergeRequestStatus.Rejected)
                 })
        {
            db.AccountMergeRequests.Add(new AccountMergeRequest
            {
                Id = Guid.NewGuid(),
                SourceUserId = source,
                TargetUserId = target,
                Status = status,
                CreatedAt = _clock.GetCurrentInstant()
            });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var requestedId = requestArchivedId ? oldestId : survivorId;
        _userService.GetUserInfoAsync(requestedId, Arg.Any<CancellationToken>())
            .Returns(new User { Id = survivorId }.ToUserInfo() with { MergedUserIds = [oldestId, archivedId] });
        var repository = new AccountMergeRepository(new TestDbContextFactory(options));

        var slices = await BuildSut(repository).ContributeForUserAsync(
            requestedId, TestContext.Current.CancellationToken);

        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            slices.Single(s => string.Equals(s.SectionName, AccountMergeService.AccountMergeRequests,
                StringComparison.Ordinal)).Data));
        var rows = json.RootElement.EnumerateArray().ToArray();
        rows.Should().HaveCount(3);
        rows.Select(r => r.GetProperty("Status").GetString())
            .Should().BeEquivalentTo(["Accepted", "Accepted", "Pending"]);
        rows.Select(r => r.GetProperty("Role").GetString())
            .Should().BeEquivalentTo(["Target", "Target", "Source"]);
    }

    [HumansFact]
    public async Task MergeAsync_HappyPath_RunsFanOutAndTombstone()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid(); var admin = Guid.NewGuid();
        SetupUsers(src, tgt);
        var merger = Substitute.For<IUserMerge>();
        _userMerges.Add(merger);

        // survivor = tgt, archived = src.
        await BuildSut().MergeAsync(tgt, src, admin, ct: TestContext.Current.CancellationToken);

        // Ordered, not transactional: the tombstone lands after every fan-out reassign.
        Received.InOrder(() =>
        {
            _ = merger.ReassignAsync(src, tgt, admin,
                Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
            _ = _userService.AnonymizeForMergeAsync(src, tgt,
                Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
        });
        await merger.Received(1).ReassignAsync(src, tgt, admin,
            Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
        await _userService.Received(1).AnonymizeForMergeAsync(src, tgt,
            Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
        await _userEmailRepo.DidNotReceive().MarkUserEmailVerifiedAsync(
            Arg.Any<Guid>(), Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MergeAsync_CompletesStartedFoldDespiteRequestCancellation(bool cancelDuringFanOut)
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid(); var admin = Guid.NewGuid();
        var pendingEmailId = Guid.NewGuid();
        using var request = new CancellationTokenSource();
        SetupUsers(src, tgt);
        var first = Substitute.For<IUserMerge>();
        var second = Substitute.For<IUserMerge>();
        first.ReassignAsync(src, tgt, admin, Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                if (cancelDuringFanOut) await request.CancelAsync();
            });
        second.ReassignAsync(src, tgt, admin, Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        _userMerges.AddRange([first, second]);
        _userService.AnonymizeForMergeAsync(src, tgt, Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                if (!cancelDuringFanOut) await request.CancelAsync();
                return true;
            });
        var pending = new AccountMergeRequest
        {
            Id = Guid.NewGuid(), SourceUserId = src, TargetUserId = tgt,
            Status = AccountMergeRequestStatus.Pending, CreatedAt = _clock.GetCurrentInstant(),
        };
        _mergeRepo.GetPendingAsync(Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<AccountMergeRequest>>([pending]);
        });

        var result = await BuildSut().MergeAsync(tgt, src, admin,
            pendingEmailIdToVerify: pendingEmailId, ct: request.Token);

        result.Refusal.Should().BeNull();
        request.IsCancellationRequested.Should().BeTrue();
        await second.Received(1).ReassignAsync(src, tgt, admin, Arg.Any<NodaTime.Instant>(), CancellationToken.None);
        await _userEmailRepo.Received(1).MarkUserEmailVerifiedAsync(pendingEmailId, Arg.Any<NodaTime.Instant>(), CancellationToken.None);
        await _userService.Received(1).AnonymizeForMergeAsync(src, tgt, Arg.Any<NodaTime.Instant>(), CancellationToken.None);
        pending.Status.Should().Be(AccountMergeRequestStatus.Accepted);
        pending.ResolvedByUserId.Should().Be(admin);
        await _mergeRepo.Received(1).UpdateAsync(pending, CancellationToken.None);
        await _audit.Received(1).LogAsync(UsersAuditActions.AccountMergeAccepted, nameof(User), src,
            Arg.Any<string>(), admin, tgt, nameof(User));
    }

    [HumansFact]
    public async Task MergeAsync_CancelledPreflightDoesNotStartTheFold()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid();
        using var request = new CancellationTokenSource();
        SetupUsers(src, tgt);
        var merger = Substitute.For<IUserMerge>();
        _userMerges.Add(merger);
        await request.CancelAsync();

        var merge = () => BuildSut().MergeAsync(tgt, src, Guid.NewGuid(), ct: request.Token);

        await merge.Should().ThrowAsync<OperationCanceledException>();
        merger.ReceivedCalls().Should().BeEmpty();
        _userEmailRepo.ReceivedCalls().Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task MergeAsync_SurvivorEqualsArchived_ReturnsRefusal()
    {
        var id = Guid.NewGuid();
        var act = () => BuildSut().MergeAsync(id, id, Guid.NewGuid(), ct: TestContext.Current.CancellationToken);
        (await act()).Refusal.Should().NotBeNull();
    }

    [HumansFact]
    public async Task MergeAsync_ArchivedMissing_ReturnsRefusal()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid();
        _userService.GetRawUserInfoAsync(tgt, Arg.Any<CancellationToken>())
            .Returns(UserInfoFactory.Create(new User { Id = tgt }, [], [], [], null, [], [], [], []));
        // archived (src) returns null by default — Substitute.For<>'s default for ValueTask<UserInfo?> is null
        var act = () => BuildSut().MergeAsync(tgt, src, Guid.NewGuid(), ct: TestContext.Current.CancellationToken);
        (await act()).Refusal.Should().NotBeNull();
    }

    [HumansFact]
    public async Task MergeAsync_ArchivedAlreadyTombstoned_ReturnsRefusal()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid();
        SetupUsers(src, tgt, sourceTombstoned: true);
        var act = () => BuildSut().MergeAsync(tgt, src, Guid.NewGuid(), ct: TestContext.Current.CancellationToken);
        (await act()).Refusal.Should().Contain("tombstoned");
    }

    [HumansFact]
    public async Task MergeAsync_FanOutThrows_LeavesArchivedAccountUntombstoned()
    {
        var src = Guid.NewGuid(); var tgt = Guid.NewGuid(); var admin = Guid.NewGuid();
        SetupUsers(src, tgt);
        var merger = Substitute.For<IUserMerge>();
        merger.ReassignAsync(src, tgt, admin, Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new InvalidOperationException("boom")));
        _userMerges.Add(merger);

        var act = () => BuildSut().MergeAsync(tgt, src, admin, ct: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        await _userService.DidNotReceive().AnonymizeForMergeAsync(
            src, tgt, Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
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
        _userService.GetRawUserInfoAsync(src, Arg.Any<CancellationToken>())
            .Returns(new User { Id = src, MergedToUserId = tgt, MergedAt = _clock.GetCurrentInstant() }.ToUserInfo());
        _userService.GetRawUserInfoAsync(tgt, Arg.Any<CancellationToken>())
            .Returns(new User { Id = tgt }.ToUserInfo());

        await BuildSut().ReconcileMergedRequestAsync(req.Id, admin, TestContext.Current.CancellationToken);

        req.Status.Should().Be(AccountMergeRequestStatus.Accepted);
        req.ResolvedByUserId.Should().Be(admin);
        await _mergeRepo.Received(1).UpdateAsync(
            Arg.Is<AccountMergeRequest>(r => r.Id == req.Id && r.Status == AccountMergeRequestStatus.Accepted),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ReconcileMergedRequestAsync_ReturnsRefusal_WhenNeitherAccountMerged()
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
        _userService.GetRawUserInfoAsync(src, Arg.Any<CancellationToken>())
            .Returns(new User { Id = src }.ToUserInfo());
        _userService.GetRawUserInfoAsync(tgt, Arg.Any<CancellationToken>())
            .Returns(new User { Id = tgt }.ToUserInfo());

        var act = () => BuildSut().ReconcileMergedRequestAsync(req.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        (await act()).Refusal.Should().Contain("not merged into each other");
    }

    [HumansFact]
    public async Task ReconcileMergedRequestAsync_ReturnsRefusal_WhenOneAccountMergedIntoThirdParty()
    {
        // Source merged into an UNRELATED third account, target still active.
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
        _userService.GetRawUserInfoAsync(src, Arg.Any<CancellationToken>())
            .Returns(new User { Id = src, MergedToUserId = third, MergedAt = _clock.GetCurrentInstant() }.ToUserInfo());
        _userService.GetRawUserInfoAsync(tgt, Arg.Any<CancellationToken>())
            .Returns(new User { Id = tgt }.ToUserInfo());

        var act = () => BuildSut().ReconcileMergedRequestAsync(req.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        (await act()).Refusal.Should().Contain("not merged into each other");
        await _mergeRepo.DidNotReceive().UpdateAsync(Arg.Any<AccountMergeRequest>(), Arg.Any<CancellationToken>());
    }
}
