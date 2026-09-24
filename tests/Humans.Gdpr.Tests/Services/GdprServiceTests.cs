using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Gdpr.Services;
using Humans.Testing;
using Humans.Users.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Gdpr.Tests.Services;

public class GdprServiceTests
{
    private static readonly Instant FixedNow = Instant.FromUtc(2026, 4, 15, 10, 30);

    private static GdprService CreateService(params IUserDataContributor[] contributors) =>
        CreateService(users: null, contributors);

    private static GdprService CreateService(
        IUserServiceRead? users, params IUserDataContributor[] contributors) =>
        CreateService(users, NullLogger<GdprService>.Instance, contributors);

    private static GdprService CreateService(
        IUserServiceRead? users, ILogger<GdprService> logger, params IUserDataContributor[] contributors) =>
        new(
            contributors,
            users ?? Substitute.For<IUserServiceRead>(),
            new FakeClock(FixedNow),
            logger);

    /// <summary>
    /// Stubs the one read the orchestrator makes: every id in <paramref name="mergedFrom"/>
    /// plus <paramref name="survivor"/> reads back as the survivor's record, which is how
    /// Users reports a merged account.
    /// </summary>
    private static IUserServiceRead StubMerged(Guid survivor, params Guid[] mergedFrom)
    {
        var resolved = MinimalUserInfo(survivor) with { MergedUserIds = mergedFrom };
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(
                (Guid)call[0] == survivor || mergedFrom.Contains((Guid)call[0]) ? resolved : null));
        return users;
    }

    private static UserInfo MinimalUserInfo(Guid id) => new(
        Id: id,
        BurnerName: "Nobody",
        IsGdprAnonymized: false,
        PreferredLanguage: "en",
        FallbackPictureUrl: null,
        CreatedAt: FixedNow,
        LastLoginAt: null,
        LastConsentReminderSentAt: null,
        DeletionRequestedAt: null,
        DeletionScheduledFor: null,
        DeletionEligibleAfter: null,
        UnsubscribedFromCampaigns: false,
        SuppressScheduleChangeEmails: false,
        MagicLinkSentAt: null,
        ContactSource: null,
        ExternalSourceId: null,
        MergedToUserId: null,
        MergedAt: null,
        IdentityEmailColumn: null,
        UserEmails: [],
        EventParticipations: [],
        ExternalLogins: [],
        Profile: null,
        CommunicationPreferences: []);

    [HumansFact]
    public async Task ExportForUserAsync_StampsExportedAtFromClock()
    {
        var service = CreateService(new FakeContributor("Profile", new { Name = "Jane" }));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.ExportedAt.Should().Be("2026-04-15T10:30:00Z");
    }

    [HumansFact]
    public async Task ExportForUserAsync_RequestedUnderMergedAwayId_NamesTheSurvivorAndItsArchivedIds()
    {
        // The key that makes the section slices legible: rows stay keyed to the archived id on
        // purpose (audit entries, consent records, roster rows, ballots), so without this the
        // reader of an export for the survivor sees rows carrying a stranger's id.
        var survivor = Guid.NewGuid();
        var archived = Guid.NewGuid();
        var service = CreateService(
            StubMerged(survivor, archived),
            new FakeContributor("Profile", new { Name = "Jane" }));

        var export = await service.ExportForUserAsync(archived, Xunit.TestContext.Current.CancellationToken);

        export.UserId.Should().Be(survivor, "the export belongs to the account that survived the merge");
        export.MergedFromUserIds.Should().Equal(archived);
    }

    [HumansFact]
    public async Task ExportForUserAsync_UnmergedAccount_CarriesNoArchivedIds()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(
            StubMerged(userId),
            new FakeContributor("Profile", new { Name = "Jane" }));

        var export = await service.ExportForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        export.UserId.Should().Be(userId);
        export.MergedFromUserIds.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ExportForUserAsync_UnknownUser_FallsBackToTheRequestedId()
    {
        var userId = Guid.NewGuid();
        var service = CreateService(new FakeContributor("Profile", new { Name = "Jane" }));

        var export = await service.ExportForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        export.UserId.Should().Be(userId);
        export.MergedFromUserIds.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ExportForUserAsync_MergesSlicesKeyedBySectionName()
    {
        var profile = new { Name = "Jane", City = "Barcelona" };
        var consents = new[] { new { Document = "Code of Conduct" } };
        var service = CreateService(
            new FakeContributor("Profile", profile),
            new FakeContributor("Consents", consents));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().HaveCount(2);
        export.Sections["Profile"].Should().BeSameAs(profile);
        export.Sections["Consents"].Should().BeSameAs(consents);
    }

    [HumansFact]
    public async Task ExportForUserAsync_DropsNullSlices()
    {
        var service = CreateService(
            new FakeContributor("Profile", new { Name = "Jane" }),
            new FakeContributor("Applications", (object?)null));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().ContainKey("Profile");
        export.Sections.Should().NotContainKey("Applications");
    }

    [HumansFact]
    public async Task ExportForUserAsync_PassesUserIdAndCancellationTokenToEveryContributor()
    {
        var userId = Guid.NewGuid();
        using var cts = new CancellationTokenSource();
        var first = new FakeContributor("A", new object());
        var second = new FakeContributor("B", new object());
        var service = CreateService(first, second);

        await service.ExportForUserAsync(userId, cts.Token);

        first.CalledWithUserId.Should().Be(userId);
        first.CalledWithToken.Should().Be(cts.Token);
        second.CalledWithUserId.Should().Be(userId);
        second.CalledWithToken.Should().Be(cts.Token);
    }

    [HumansFact]
    public async Task ExportForUserAsync_FailsLoudlyOnDuplicateSectionName()
    {
        var service = CreateService(
            new FakeContributor("Profile", new { A = 1 }),
            new FakeContributor("Profile", new { B = 2 }));

        var act = async () => await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Profile*");
    }

    [HumansFact]
    public async Task ExportForUserAsync_LogsAndContinuesWhenExportedSectionHasNoErasureDeclaration()
    {
        var logger = new CapturingLogger<GdprService>();
        var service = CreateService(
            users: null,
            logger,
            new UndeclaredErasureContributor(),
            new FakeContributor("Consents", new { Document = "Code of Conduct" }));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().ContainKey("Profile", "the undeclared slice is still the person's data");
        export.Sections.Should().ContainKey("Consents", "other contributors still complete");
        logger.Entries.Should().Contain(e =>
            e.Level == LogLevel.Error &&
            e.Message.Contains("Profile") &&
            e.Message.Contains("UndeclaredErasureContributor"));
    }

    private sealed class UndeclaredErasureContributor : IUserDataContributor
    {
        public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UserDataSlice>>([new UserDataSlice("Profile", new { Name = "Jane" })]);

        // Deliberately empty: "Profile" is exported but never declared for erasure.
        public IReadOnlyDictionary<string, string?> ErasureDeclaration =>
            new Dictionary<string, string?>(StringComparer.Ordinal);

        public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    }

    [HumansFact]
    public async Task ExportForUserAsync_PropagatesContributorFailure()
    {
        var boom = new InvalidOperationException("boom");
        var logger = new CapturingLogger<GdprService>();
        var service = CreateService(
            users: null,
            logger,
            new FakeContributor("Profile", new { A = 1 }),
            new FakeContributor("Applications", boom));

        var act = async () => await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("export contributor") &&
            entry.Message.Contains("FakeContributor"));
    }

    [HumansFact]
    public async Task ExportForUserAsync_WithNoContributors_ReturnsEmptySectionBag()
    {
        var service = CreateService();

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().BeEmpty();
        export.ExportedAt.Should().NotBeNullOrEmpty();
    }

    [HumansFact]
    public async Task ExportForUserAsync_FlattensMultipleSlicesFromOneContributor()
    {
        var service = CreateService(new FakeContributor(
            new UserDataSlice("Profile", new { Name = "Jane" }),
            new UserDataSlice("ContactFields", new[] { new { Field = "email" } }),
            new UserDataSlice("Languages", new[] { new { Code = "es" } })));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().HaveCount(3);
        export.Sections.Should().ContainKey("Profile");
        export.Sections.Should().ContainKey("ContactFields");
        export.Sections.Should().ContainKey("Languages");
    }

    [HumansFact]
    public async Task ExportForUserAsync_EmptyCollectionSliceSurvivesAsEmptyList()
    {
        // Empty collections MUST round-trip to "[]" in the JSON: a collection key
        // is always present, never omitted, even when the user has no records.
        var emptyConsents = Array.Empty<object>();
        var service = CreateService(
            new FakeContributor("Profile", new { Name = "Jane" }),
            new FakeContributor("Consents", emptyConsents));

        var export = await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        export.Sections.Should().ContainKey("Consents",
            "an empty collection slice must NOT be dropped by the orchestrator");
        export.Sections["Consents"].Should().BeSameAs(emptyConsents);
    }

    [HumansFact]
    public async Task ExportForUserAsync_CallsContributorsOneAtATime()
    {
        var log = new ContributorCallLog();
        var service = CreateService(
            new ProbeContributor("A", log),
            new ProbeContributor("B", log),
            new ProbeContributor("C", log));

        await service.ExportForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        log.MaxConcurrent.Should().Be(1,
            "the fan-out is sequential — a Task.WhenAll would overlap contributors, and one at " +
            "a time is what keeps failure attribution and log order plain");
        log.Order.Should().Equal("A", "B", "C");
    }

    // ==========================================================================
    // EraseForUserAsync (Article 17 fan-out)
    // ==========================================================================

    [HumansFact]
    public async Task EraseForUserAsync_RunsEveryContributor()
    {
        var a = new RecordingContributor("Issues");
        var b = new RecordingContributor("Consents");
        var service = CreateService(a, b);
        var id = Guid.NewGuid();

        await service.EraseForUserAsync(id, Xunit.TestContext.Current.CancellationToken);

        a.ErasedIds.Should().Equal(id);
        b.ErasedIds.Should().Equal(id);
    }

    [HumansFact]
    public async Task EraseForUserAsync_ErasesTheErasesLastContributorLast()
    {
        // Sections that must reach an external processor (the Workspace suspend) need the
        // human's addresses, which the identity contributor is about to drop. Registration
        // order is identity-first here on purpose: ordering is derived from ErasesLast, not
        // registration order.
        var order = new List<string>();
        var account = new RecordingContributor("Account", order) { ErasesLast = true };
        var section = new RecordingContributor("Issues", order);
        var service = CreateService(account, section);

        await service.EraseForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        order.Should().Equal("Issues", "Account");
    }

    [HumansFact]
    public async Task EraseForUserAsync_PropagatesContributorFailureAndStopsBeforeAccount()
    {
        // A throwing contributor aborts the run before the identity collapse.
        // What the caller then does with its deletion markers is Users' concern, not this
        // orchestrator's, and nothing here observes it.
        var boom = new RecordingContributor("Issues") { Throw = new InvalidOperationException("boom") };
        var account = new RecordingContributor("Account") { ErasesLast = true };
        var logger = new CapturingLogger<GdprService>();
        var service = CreateService(users: null, logger, boom, account);

        var act = async () => await service.EraseForUserAsync(
            Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("boom");
        account.ErasedIds.Should().BeEmpty();
        logger.Entries.Should().Contain(entry =>
            entry.Level == LogLevel.Error &&
            entry.Message.Contains("erasure contributor") &&
            entry.Message.Contains("RecordingContributor"));
    }

    private sealed class RecordingContributor(string section, List<string>? order = null) : IUserDataContributor
    {
        public Exception? Throw { get; init; }
        public bool ErasesLast { get; init; }
        public List<Guid> ErasedIds { get; } = [];

        public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<UserDataSlice>>([]);

        public IReadOnlyDictionary<string, string?> ErasureDeclaration =>
            new Dictionary<string, string?>(StringComparer.Ordinal) { [section] = null };

        public Task EraseForUserAsync(Guid userId, CancellationToken ct)
        {
            order?.Add(section);
            ErasedIds.Add(userId);
            if (Throw is not null) throw Throw;
            return Task.CompletedTask;
        }
    }

    private sealed class ContributorCallLog
    {
        private int _inFlight;

        public int MaxConcurrent { get; private set; }
        public List<string> Order { get; } = [];

        public void Enter(string name)
        {
            Order.Add(name);
            _inFlight++;
            if (_inFlight > MaxConcurrent) MaxConcurrent = _inFlight;
        }

        public void Exit() => _inFlight--;
    }

    /// <summary>
    /// Yields mid-call, so an orchestrator that awaited its contributors concurrently
    /// would leave two of these in flight at once and push MaxConcurrent above 1.
    /// </summary>
    private sealed class ProbeContributor(string name, ContributorCallLog log) : IUserDataContributor
    {
        public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
        {
            log.Enter(name);
            await Task.Yield();
            await Task.Yield();
            log.Exit();
            return [new UserDataSlice(name, new object())];
        }

        public IReadOnlyDictionary<string, string?> ErasureDeclaration =>
            new Dictionary<string, string?>(StringComparer.Ordinal) { [name] = null };

        public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class FakeContributor : IUserDataContributor
    {
        private readonly UserDataSlice[] _slices;
        private readonly Exception? _throw;

        public FakeContributor(string sectionName, object? data)
        {
            _slices = [new UserDataSlice(sectionName, data)];
        }

        public FakeContributor(string sectionName, Exception throwOnCall)
        {
            _slices = [new UserDataSlice(sectionName, null)];
            _throw = throwOnCall;
        }

        public FakeContributor(params UserDataSlice[] slices)
        {
            _slices = slices;
        }

        public Guid? CalledWithUserId { get; private set; }
        public CancellationToken? CalledWithToken { get; private set; }

        public Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct)
        {
            CalledWithUserId = userId;
            CalledWithToken = ct;
            if (_throw is not null) throw _throw;
            return Task.FromResult<IReadOnlyList<UserDataSlice>>(_slices);
        }

        public IReadOnlyDictionary<string, string?> ErasureDeclaration =>
            _slices.ToDictionary(s => s.SectionName, _ => (string?)null, StringComparer.Ordinal);

        // Export-only fake: erasure is exercised through RecordingContributor.
        public Task EraseForUserAsync(Guid userId, CancellationToken ct) => Task.CompletedTask;
    }
}
