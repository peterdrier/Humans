using Humans.AuditLog.Contracts;
using Humans.Base.Constants;
using Humans.Email.Contracts;
using Humans.Auth.Contracts;
using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Services;
using Humans.Governance.Services.Dtos;
using Humans.Notifications.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

using Xunit;

namespace Humans.Governance.Tests.Infrastructure;

/// <summary>
/// Shared wiring for <see cref="AssemblyVoteService"/> tests: an in-memory
/// <see cref="GovernanceDbContext"/>, a real <see cref="AssemblyVoteRepository"/> over it, a
/// <see cref="FakeClock"/> the tests move by hand, and substitutes for every crosscut the
/// service depends on. Composed by each test class (not inherited) — same reasoning as
/// <c>ApplicationDecisionServiceTests</c>: this stays a plain field, not a base class with a
/// widening surface of its own.
/// </summary>
internal sealed class AssemblyVoteServiceFixture : IDisposable
{
    public readonly FakeClock Clock = new(Instant.FromUtc(2026, 9, 1, 12, 0));
    public readonly IAuditLogService Audit = Substitute.For<IAuditLogService>();
    public readonly IApplicationRepository Applications = Substitute.For<IApplicationRepository>();
    public readonly IRoleAssignmentService RoleAssignments = Substitute.For<IRoleAssignmentService>();
    public readonly ITeamServiceRead Teams = Substitute.For<ITeamServiceRead>();
    public readonly IUserServiceRead Users = Substitute.For<IUserServiceRead>();
    public readonly IUserEmailService UserEmails = Substitute.For<IUserEmailService>();
    public readonly IEmailService Email = Substitute.For<IEmailService>();
    public readonly IEmailMessageFactory Messages = Substitute.For<IEmailMessageFactory>();
    public readonly INotificationEmitter Notifications = Substitute.For<INotificationEmitter>();
    public readonly INotificationAutoResolve NotificationResolve = Substitute.For<INotificationAutoResolve>();

    private readonly TestDbContextFactory<GovernanceDbContext> _factory =
        new(new DbContextOptionsBuilder<GovernanceDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    public readonly GovernanceDbContext Db;
    public readonly AssemblyVoteRepository Repository;
    public readonly AssemblyVoteService Service;

    public AssemblyVoteServiceFixture()
    {
        Db = _factory.CreateDbContext();
        Repository = new AssemblyVoteRepository(_factory);

        // Empty by default; tests stub only what they need on top of this.
        Applications.GetActiveApprovedTierUserIdsAsync(
                Arg.Any<MembershipTier>(), Arg.Any<LocalDate>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([]));
        RoleAssignments.GetActiveUserIdsInRoleAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Guid>>([]));
        Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));
        UserEmails.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>()));

        Service = new AssemblyVoteService(
            Repository,
            Applications,
            RoleAssignments,
            Teams,
            Users,
            UserEmails,
            Email,
            Messages,
            Notifications,
            NotificationResolve,
            Audit,
            Clock,
            NullLogger<AssemblyVoteService>.Instance);
    }

    public void Dispose() => Db.Dispose();

    /// <summary>Marks a set of user ids Active, the only state a roster candidate survives.</summary>
    public void StubActiveUsers(params Guid[] userIds)
    {
        var map = userIds.ToDictionary(
            id => id,
            id => new User
            {
                Id = id,
                DisplayName = "Member " + id,
                UserName = id + "@example.org",
                Email = id + "@example.org",
                PreferredLanguage = "en",
                State = UserState.Active
            }.ToUserInfo());

        Users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(map));

        UserEmails.GetNotificationTargetEmailsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, string>>(
                userIds.ToDictionary(id => id, id => id + "@example.org")));
    }

    public async Task<AssemblyVote> AddVoteAsync(
        AssemblyVoteStatus status = AssemblyVoteStatus.Open,
        AssemblyVoteKind kind = AssemblyVoteKind.YesNo,
        RequiredMajority requiredMajority = RequiredMajority.Simple,
        IndicativeAudience indicativeAudience = IndicativeAudience.None,
        BallotDisclosure ballotDisclosure = BallotDisclosure.BoardOnly,
        Instant? closesAt = null,
        IReadOnlyList<(string Key, int Order)>? options = null)
    {
        var now = Clock.GetCurrentInstant();
        var vote = new AssemblyVote
        {
            Id = Guid.NewGuid(),
            Title = new GovernanceLocalizedText(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Test vote" }),
            OfficialText = new GovernanceLocalizedText(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = "Text" }),
            OfficialCulture = "en",
            Kind = kind,
            RequiredMajority = requiredMajority,
            IndicativeAudience = indicativeAudience,
            BallotDisclosure = ballotDisclosure,
            Status = status,
            ClosesAt = closesAt ?? now + Duration.FromDays(1),
            OpenedAt = status == AssemblyVoteStatus.Draft ? null : now - Duration.FromHours(1),
            CreatedByUserId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };

        if (status is AssemblyVoteStatus.Closed or AssemblyVoteStatus.Cancelled)
        {
            vote.ClosedAt = now - Duration.FromMinutes(30);
        }

        foreach (var (key, order) in options ?? [])
        {
            vote.Options.Add(new AssemblyVoteOption
            {
                Id = Guid.NewGuid(),
                VoteId = vote.Id,
                Order = order,
                Key = key,
                Label = new GovernanceLocalizedText(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["en"] = key })
            });
        }

        Db.AssemblyVotes.Add(vote);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();
        return vote;
    }

    /// <summary>A minimal valid draft for <paramref name="vote"/>, with the given option keys.</summary>
    public AssemblyVoteDraft DraftFor(
        AssemblyVote vote, AssemblyVoteKind kind, IReadOnlyList<string> optionKeys)
    {
        ArgumentNullException.ThrowIfNull(vote);
        ArgumentNullException.ThrowIfNull(optionKeys);

        return new AssemblyVoteDraft(
            Text("Test vote"),
            Text("Text"),
            "en",
            null,
            kind,
            RequiredMajority.Simple,
            IndicativeAudience.None,
            BallotDisclosure.BoardOnly,
            null,
            vote.ClosesAt,
            [.. optionKeys.Select((key, order) => new AssemblyVoteDraftOption(key, order, Text(key)))]);
    }

    private static Dictionary<string, string> Text(string value) =>
        new(StringComparer.OrdinalIgnoreCase) { ["en"] = value };

    public async Task<AssemblyVoteRoster> AddRosterRowAsync(
        Guid voteId, Guid userId, bool isOfficial, MembershipTier tier = MembershipTier.Volunteer,
        bool isBoardMember = false)
    {
        var roster = new AssemblyVoteRoster
        {
            Id = Guid.NewGuid(),
            VoteId = voteId,
            UserId = userId,
            Tier = tier,
            IsBoardMember = isBoardMember,
            IsOfficial = isOfficial
        };
        Db.AssemblyVoteRosterEntries.Add(roster);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();
        return roster;
    }

    public async Task<AssemblyBallot> AddBallotAsync(
        Guid voteId, Guid rosterId, AssemblyBallotChoice choice, IReadOnlyList<string>? ranking = null,
        int revision = 1, Instant? castAt = null)
    {
        var now = castAt ?? Clock.GetCurrentInstant();
        var ballot = new AssemblyBallot
        {
            Id = Guid.NewGuid(),
            VoteId = voteId,
            RosterId = rosterId,
            Choice = choice,
            Ranking = ranking,
            Revision = revision,
            CastAt = now,
            UpdatedAt = now
        };
        Db.AssemblyBallots.Add(ballot);
        await Db.SaveChangesAsync(Xunit.TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();
        return ballot;
    }
}
