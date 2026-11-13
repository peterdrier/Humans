using Humans.Email.Contracts;
using System.Transactions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Humans.Auth.Contracts;
using Humans.Base.Interfaces;
using Humans.Teams.Contracts;
using Microsoft.Extensions.Localization;
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

    [HumansFact]
    public async Task RejectAsync_Rollback_RestoresThePendingEmailInTheCache()
    {
        var ct = TestContext.Current.CancellationToken;
        var target = Guid.NewGuid();
        var email = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = target,
            Email = "pending@example.org",
            IsVerified = false
        };
        IReadOnlyList<UserEmail> stored = [email];
        UserInfo Snapshot() => UserInfo.Create(new User { Id = target }, stored, [], null);
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ => (IReadOnlyCollection<UserInfo>)[Snapshot()]);
        _userService.GetUserInfoAsync(target, Arg.Any<CancellationToken>())
            .Returns(_ => ValueTask.FromResult<UserInfo?>(Snapshot()));
        var services = new ServiceCollection();
        services.AddKeyedScoped<IUserManagement>(CachingUserService.InnerServiceKey, (_, _) => _userService);
        var cache = new CachingUserService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingUserService>.Instance);
        (await cache.GetRawUserInfoAsync(target, ct))!.UserEmails.Should().ContainSingle();
        var request = new AccountMergeRequest
        {
            Id = Guid.NewGuid(),
            TargetUserId = target,
            SourceUserId = Guid.NewGuid(),
            PendingEmailId = email.Id,
            Email = email.Email,
            Status = AccountMergeRequestStatus.Pending
        };
        _mergeRepo.GetByIdPlainAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        _userEmailRepo.RemoveUserEmailByIdAsync(email.Id, Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            Transaction.Current!.TransactionCompleted += (_, args) =>
            {
                if (args.Transaction!.TransactionInformation.Status == TransactionStatus.Aborted)
                    stored = [email];
            };
            stored = [];
            // Models Identity's save interceptor reading its transaction's provisional rows.
            await cache.RefreshUserEmailsAsync(target, CancellationToken.None);
            (await cache.GetRawUserInfoAsync(target, ct))!.UserEmails.Should().BeEmpty();
            return true;
        });
        var failure = new IOException("Metadata unavailable");
        _mergeRepo.UpdateAsync(request, Arg.Any<CancellationToken>()).Returns(Task.FromException(failure));
        var service = new AccountMergeService(_mergeRepo, _userEmailRepo, _audit, cache,
            NullLogger<AccountMergeService>.Instance, _clock, _userMerges, _userService);

        var reject = () => service.RejectAsync(request.Id, Guid.NewGuid(), ct: ct);
        (await reject.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        stored.Should().ContainSingle();
        (await cache.GetRawUserInfoAsync(target, ct))!.UserEmails.Should().ContainSingle(e => e.Id == email.Id);
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectAsync_AuditFailure_RefreshesWithoutMaskingTheError(bool refreshFails)
    {
        var request = new AccountMergeRequest
        {
            Id = Guid.NewGuid(),
            TargetUserId = Guid.NewGuid(),
            SourceUserId = Guid.NewGuid(),
            PendingEmailId = Guid.NewGuid(),
            Email = "pending@example.org",
            Status = AccountMergeRequestStatus.Pending
        };
        _mergeRepo.GetByIdPlainAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        var actor = Guid.NewGuid();
        var failure = new IOException("Audit unavailable");
        using var cancellation = new CancellationTokenSource();
        _audit.LogAsync(UsersAuditActions.AccountMergeRejected, nameof(AccountMergeRequest), request.Id,
            Arg.Any<string>(), actor).Returns(async _ =>
        {
            await cancellation.CancelAsync();
            throw failure;
        });
        if (refreshFails)
            _userInfoRefresher.RefreshAsync(request.TargetUserId, Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>())
                .Returns(Task.FromException(new IOException("Refresh unavailable")));

        var reject = () => BuildSut().RejectAsync(request.Id, actor, ct: cancellation.Token);
        (await reject.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        await _userInfoRefresher.Received(1).RefreshAsync(
            request.TargetUserId, CancellationToken.None, Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MergeAsync_CommittedContactMoveThenFailure_RefreshesBothAccounts(bool lostAcknowledgement)
    {
        var ct = TestContext.Current.CancellationToken;
        var source = new User { Id = Guid.NewGuid(), BurnerName = "Source" };
        var target = new User { Id = Guid.NewGuid(), BurnerName = "Target" };
        var sourceProfile = new Profile { Id = Guid.NewGuid(), UserId = source.Id, FirstName = "Source", LastName = "Human" };
        var targetProfile = new Profile { Id = Guid.NewGuid(), UserId = target.Id, FirstName = "Target", LastName = "Human" };
        var field = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = sourceProfile.Id,
            FieldType = ContactFieldType.Phone,
            Value = "contact-number",
            Visibility = ContactFieldVisibility.BoardOnly
        };
        var options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using (var seed = new UsersDbContext(options))
        {
            seed.Users.AddRange(source, target);
            seed.Profiles.AddRange(sourceProfile, targetProfile);
            seed.ContactFields.Add(field);
            await seed.SaveChangesAsync(ct);
        }
        async Task<IReadOnlyCollection<UserInfo>> ReadUsersAsync(CancellationToken token)
        {
            await using var read = new UsersDbContext(options);
            var fields = await read.ContactFields.AsNoTracking().ToListAsync(token);
            return new[] { (source, sourceProfile), (target, targetProfile) }
                .Select(pair => UserInfoFactory.Create(pair.Item1, [], [], pair.Item2,
                    fields.Where(f => f.ProfileId == pair.Item2.Id).ToList(), [], [])).ToList();
        }
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(call => ReadUsersAsync(call.Arg<CancellationToken>()));
        _userService.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(ReadUserAsync(call.Arg<Guid>(), call.Arg<CancellationToken>())));
        async Task<UserInfo?> ReadUserAsync(Guid id, CancellationToken token) =>
            (await ReadUsersAsync(token)).Single(u => u.Id == id);
        var services = new ServiceCollection();
        services.AddKeyedScoped<IUserManagement>(CachingUserService.InnerServiceKey, (_, _) => _userService);
        var cache = new CachingUserService(services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingUserService>.Instance);
        (await cache.GetRawUserInfoAsync(source.Id, ct))!.Profile!.ContactFields.Should().ContainSingle();
        (await cache.GetRawUserInfoAsync(target.Id, ct))!.Profile!.ContactFields.Should().BeEmpty();
        var failure = new IOException("Merge step failed");
        var writeOptions = new DbContextOptionsBuilder<UsersDbContext>(options);
        if (lostAcknowledgement) writeOptions.AddInterceptors(new LostContactMergeAcknowledgement(failure));
        var contacts = new ContactFieldService(new UserRepository(new TestDbContextFactory(writeOptions.Options), _clock),
            cache, Substitute.For<ITeamService>(), Substitute.For<IRoleAssignmentService>(), cache,
            _clock, NullLogger<ContactFieldService>.Instance, Substitute.For<IStringLocalizer<UsersResource>>(), Substitute.For<IUserEmailService>(), Substitute.For<ICommunicationPreferenceService>());
        var later = Substitute.For<IUserMerge>();
        later.ReassignAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(failure));
        var service = new AccountMergeService(_mergeRepo, new UserRepository(new TestDbContextFactory(options), _clock),
            _audit, cache, NullLogger<AccountMergeService>.Instance, _clock, [contacts, later], cache);

        var merge = () => service.MergeAsync(target.Id, source.Id, Guid.NewGuid(), ct: ct);
        (await merge.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        await using var verify = new UsersDbContext(options);
        (await verify.ContactFields.SingleAsync(ct)).ProfileId.Should().Be(targetProfile.Id);
        (await cache.GetRawUserInfoAsync(source.Id, ct))!.Profile!.ContactFields.Should().BeEmpty();
        (await cache.GetRawUserInfoAsync(target.Id, ct))!.Profile!.ContactFields.Should().ContainSingle(f => f.Id == field.Id);
        await _userService.DidNotReceive().AnonymizeForMergeAsync(
            source.Id, target.Id, Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>());
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    private sealed class LostContactMergeAcknowledgement(IOException failure) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default) => throw failure;
    }

    [HumansFact]
    public async Task MergeAsync_RefreshFailure_StillRefreshesTheOtherAccountAndPreservesTheMergeError()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        SetupUsers(source, target);
        var failure = new IOException("Contributor failed");
        var merger = Substitute.For<IUserMerge>();
        merger.ReassignAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<NodaTime.Instant>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(failure));
        _userMerges.Add(merger);
        _userInfoRefresher.RefreshAsync(source, Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromException(new IOException("Refresh failed")));

        var merge = () => BuildSut().MergeAsync(target, source, Guid.NewGuid(), ct: TestContext.Current.CancellationToken);
        (await merge.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);

        await _userInfoRefresher.Received(1).RefreshAsync(source, CancellationToken.None, Arg.Any<string>(), Arg.Any<string>());
        await _userInfoRefresher.Received(1).RefreshAsync(target, CancellationToken.None, Arg.Any<string>(), Arg.Any<string>());
    }

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
    public async Task RejectAsync_AuditsOnlyAfterItsCompoundWriteCommits(bool metadataFails)
    {
        var actor = Guid.NewGuid();
        var request = new AccountMergeRequest
        {
            Id = Guid.NewGuid(),
            SourceUserId = Guid.NewGuid(),
            TargetUserId = Guid.NewGuid(),
            PendingEmailId = Guid.NewGuid(),
            Email = "pending@example.org",
            Status = AccountMergeRequestStatus.Pending
        };
        _mergeRepo.GetByIdPlainAsync(request.Id, Arg.Any<CancellationToken>()).Returns(request);
        Transaction? transaction = null;
        string? updateTransactionId = null;
        _userEmailRepo.RemoveUserEmailByIdAsync(request.PendingEmailId, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            transaction = Transaction.Current?.Clone();
            return Task.FromResult(true);
        });
        var failure = new IOException("Merge metadata unavailable");
        _mergeRepo.UpdateAsync(request, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            updateTransactionId = Transaction.Current?.TransactionInformation.LocalIdentifier;
            return metadataFails ? Task.FromException(failure) : Task.CompletedTask;
        });
        var refreshAfterDisposal = false;
        _userInfoRefresher.RefreshAsync(request.TargetUserId, Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().Should().Be(CancellationToken.None);
                refreshAfterDisposal = Transaction.Current is null
                    && transaction?.TransactionInformation.Status == (metadataFails ? TransactionStatus.Aborted : TransactionStatus.Committed);
                return Task.CompletedTask;
            });
        var auditAfterCommit = false;
        _audit.LogAsync(UsersAuditActions.AccountMergeRejected, nameof(AccountMergeRequest), request.Id,
            Arg.Any<string>(), actor).Returns(_ =>
        {
            auditAfterCommit = Transaction.Current is null
                && transaction?.TransactionInformation.Status == TransactionStatus.Committed;
            return Task.CompletedTask;
        });
        Func<Task> reject = () => BuildSut().RejectAsync(request.Id, actor, "Declined", TestContext.Current.CancellationToken);
        try
        {
            if (metadataFails)
            {
                (await reject.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
                _audit.ReceivedCalls().Should().BeEmpty();
            }
            else
            {
                await reject.Should().NotThrowAsync();
                auditAfterCommit.Should().BeTrue();
                request.Status.Should().Be(AccountMergeRequestStatus.Rejected);
                request.ResolvedByUserId.Should().Be(actor);
                request.AdminNotes.Should().Be("Declined");
            }
            refreshAfterDisposal.Should().BeTrue();
            transaction.Should().NotBeNull();
            updateTransactionId.Should().Be(transaction!.TransactionInformation.LocalIdentifier);
            transaction.TransactionInformation.Status.Should().Be(metadataFails ? TransactionStatus.Aborted : TransactionStatus.Committed);
        }
        finally
        {
            transaction?.Dispose();
        }
    }

    [HumansTheory]
    [InlineData("alex_smith@example.com", null, "alexXsmith@example.com", false)]
    [InlineData("alex_smith@example.com", null, "ALEX_SMITH@example.com", true)]
    [InlineData("alex%smith@example.com", null, "alexXsmith@example.com", false)]
    [InlineData("alex%smith@example.com", null, "ALEX%SMITH@example.com", true)]
    [InlineData("alex_smith@gmail.com", "alex_smith@googlemail.com", "alexXsmith@googlemail.com", false)]
    [InlineData("alex_smith@gmail.com", "alex_smith@googlemail.com", "ALEX_SMITH@GOOGLEMAIL.COM", true)]
    public async Task HasPendingForUserAndEmailAsync_MatchesLiteralAddressesWithinPendingTargetRequests(
        string email, string? alternate, string pendingEmail, bool expected)
    {
        var sourceId = Guid.NewGuid();
        var targetId = Guid.NewGuid();
        var otherTargetId = Guid.NewGuid();
        var options = new DbContextOptionsBuilder<UsersDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        await using var db = new UsersDbContext(options);
        db.Users.AddRange(new[] { sourceId, targetId, otherTargetId }.Select(id => new User { Id = id }));
        foreach (var (target, address, status) in new[]
                 {
                     (targetId, pendingEmail, AccountMergeRequestStatus.Pending),
                     (otherTargetId, email, AccountMergeRequestStatus.Pending),
                     (targetId, email, AccountMergeRequestStatus.Accepted),
                     (targetId, alternate ?? email, AccountMergeRequestStatus.Rejected)
                 })
        {
            db.AccountMergeRequests.Add(new AccountMergeRequest
            {
                Id = Guid.NewGuid(),
                SourceUserId = sourceId,
                TargetUserId = target,
                Email = address,
                Status = status,
                CreatedAt = _clock.GetCurrentInstant()
            });
        }
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var repository = new AccountMergeRepository(new TestDbContextFactory(options));

        var result = await BuildSut(repository).HasPendingForUserAndEmailAsync(
            targetId, email, alternate, TestContext.Current.CancellationToken);

        result.Should().Be(expected);
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
            Id = Guid.NewGuid(),
            SourceUserId = src,
            TargetUserId = tgt,
            Status = AccountMergeRequestStatus.Pending,
            CreatedAt = _clock.GetCurrentInstant(),
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
            .Returns(UserInfoFactory.Create(new User { Id = tgt }, [], [], null, [], [], []));
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
