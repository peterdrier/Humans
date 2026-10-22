using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Email.Services;
using Humans.Tickets.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Email.Tests.Services;

public sealed class CommunicationPreferenceServiceTests : IDisposable
{
    private readonly EmailDbContext _db;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 1, 12, 0));
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly ITicketService _tickets = Substitute.For<ITicketService>();
    private readonly Dictionary<Guid, List<LegacyCommunicationPreferenceInfo>> _legacy = [];
    private readonly Dictionary<Guid, Guid> _mergedTo = [];
    private readonly IDataProtectionProvider _dataProtection = DataProtectionProvider.Create("TestApp");
    private readonly CommunicationPreferenceService _service;

    public CommunicationPreferenceServiceTests()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new EmailDbContext(options);

        // Raw reads answer by id; the resolving read follows a merge to its survivor.
        _users.GetRawUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<UserInfo?>(Info(ci.Arg<Guid>())));
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<Guid>();
                return new ValueTask<UserInfo?>(Info(_mergedTo.GetValueOrDefault(id, id)));
            });
        _users.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyCollection<UserInfo>>([.. _legacy.Keys.Select(Info)]));

        var tokenProvider = new UnsubscribeTokenProvider(
            _dataProtection, Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" }),
            NullLogger<UnsubscribeTokenProvider>.Instance);

        _service = new CommunicationPreferenceService(
            new CommunicationPreferenceRepository(new TestDbContextFactory<EmailDbContext>(options)),
            _users, tokenProvider, _tickets, _clock, _audit, NullLogger<CommunicationPreferenceService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UserInfo Info(Guid id) => UserInfo.Create(
        new User { Id = id, DisplayName = "", PreferredLanguage = "en", CreatedAt = Instant.MinValue },
        [], [], [], profile: null, _legacy.GetValueOrDefault(id) ?? []);

    private void SeedLegacy(Guid userId, LegacyMessageCategory category, bool optedOut,
        bool inboxEnabled = true, Instant? updatedAt = null, string source = "Profile", Instant? subscribedAt = null)
    {
        if (!_legacy.TryGetValue(userId, out var rows))
            _legacy[userId] = rows = [];
        rows.Add(new LegacyCommunicationPreferenceInfo(Guid.NewGuid(), category, optedOut, inboxEnabled,
            updatedAt ?? _clock.GetCurrentInstant(), source, subscribedAt));
    }

    private async Task SeedRowAsync(Guid userId, MessageCategory category, bool? optedOut,
        bool inboxEnabled = true, Instant? updatedAt = null, string source = "Profile", Instant? subscribedAt = null)
    {
        _db.CommunicationPreferences.Add(new CommunicationPreference
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Category = category,
            OptedOut = optedOut,
            InboxEnabled = inboxEnabled,
            UpdatedAt = updatedAt ?? _clock.GetCurrentInstant(),
            UpdateSource = source,
            SubscribedAt = subscribedAt,
        });
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
    }

    private Task<List<CommunicationPreference>> RowsAsync(Guid userId) =>
        _db.CommunicationPreferences.AsNoTracking().Where(r => r.UserId == userId).ToListAsync(Ct);

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FullPreferenceUpdate_UsesTheKnownActorOrKeepsAnonymousAttribution(bool authenticated)
    {
        var userId = Guid.NewGuid();
        var actorId = Guid.NewGuid();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
            optedOut: false, inboxEnabled: false, authenticated ? "Profile" : "MagicLink",
            actorUserId: authenticated ? actorId : null, cancellationToken: Ct);

        var audit = _audit.ReceivedCalls().Should().ContainSingle().Subject.GetArguments();
        audit[0].Should().Be(AuditAction.CommunicationPreferenceChanged);
        audit[2].Should().Be(userId);
        audit[4].Should().Be(authenticated ? actorId : nameof(CommunicationPreferenceService));
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmailOnlyUpdate_PreservesInboxOrUsesTheDomainDefault(bool existing)
    {
        var userId = Guid.NewGuid();
        if (existing)
            await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: true, inboxEnabled: false);

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "MailerLite", cancellationToken: Ct);

        var row = (await RowsAsync(userId)).Should().ContainSingle().Subject;
        row.OptedOut.Should().BeFalse();
        row.InboxEnabled.Should().Be(!existing);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnchangedPreference_DoesNotRewriteMetadataOrAudit(bool fullUpdate)
    {
        var userId = Guid.NewGuid();
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
            optedOut: false, inboxEnabled: false, source: "Profile", actorUserId: userId, cancellationToken: Ct);
        var original = (await RowsAsync(userId)).Single();
        _clock.Advance(Duration.FromHours(1));
        _audit.ClearReceivedCalls();

        if (fullUpdate)
            await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
                optedOut: false, inboxEnabled: false, source: "Guest", actorUserId: userId, cancellationToken: Ct);
        else
            await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "MailerLite", cancellationToken: Ct);

        var unchanged = (await RowsAsync(userId)).Single();
        unchanged.UpdatedAt.Should().Be(original.UpdatedAt);
        unchanged.SubscribedAt.Should().Be(original.SubscribedAt);
        unchanged.UpdateSource.Should().Be("Profile");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task IsOptedOut_MarketingDefaultsToOptedOutWithoutWriting()
    {
        var userId = Guid.NewGuid();

        (await _service.IsOptedOutAsync(userId, MessageCategory.Marketing, Ct)).Should().BeTrue();
        (await _db.CommunicationPreferences.AnyAsync(Ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task AlwaysOnCategories_CannotBeChangedOrReset_AndNeverReadOptedOut()
    {
        var userId = Guid.NewGuid();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.System, optedOut: true, source: "Test", cancellationToken: Ct);
        await _service.UpdatePreferenceAsync(userId, MessageCategory.CampaignCodes, optedOut: true,
            inboxEnabled: false, source: "Profile", actorUserId: userId, cancellationToken: Ct);
        await _service.ResetPreferenceAsync(userId, MessageCategory.System, "Test", Ct);

        (await _db.CommunicationPreferences.AnyAsync(Ct)).Should().BeFalse();
        _audit.ReceivedCalls().Should().BeEmpty();
        (await _service.IsOptedOutAsync(userId, MessageCategory.System, Ct)).Should().BeFalse();
        (await _service.IsOptedOutAsync(userId, MessageCategory.CampaignCodes, Ct)).Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MemberToggle_TicketingIsRefusedWhileTheMemberHoldsATicket(bool holdsTicket)
    {
        var userId = Guid.NewGuid();
        _tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(holdsTicket ? 1 : 0, []));

        var applied = await _service.UpdatePreferenceAsync(userId, MessageCategory.Ticketing,
            optedOut: true, inboxEnabled: false, source: "Guest", actorUserId: userId, cancellationToken: Ct);

        applied.Should().Be(!holdsTicket);
        (await RowsAsync(userId)).Should().HaveCount(holdsTicket ? 0 : 1);
        _audit.ReceivedCalls().Should().HaveCount(holdsTicket ? 0 : 1);
    }

    [HumansFact]
    public async Task MemberToggle_OtherCategoriesIgnoreTheTicketLock()
    {
        var userId = Guid.NewGuid();
        _tickets.GetUserTicketHoldingsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(1, []));

        (await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
            optedOut: true, inboxEnabled: false, source: "Guest", actorUserId: userId, cancellationToken: Ct))
            .Should().BeTrue();
        await _tickets.DidNotReceive().GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AcceptsFacilitatedMessages_DefaultsOn_AndFollowsAnOptOut()
    {
        var userId = Guid.NewGuid();
        (await _service.AcceptsFacilitatedMessagesAsync(userId, Ct)).Should().BeTrue();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.FacilitatedMessages, optedOut: true, source: "Test", cancellationToken: Ct);

        (await _service.AcceptsFacilitatedMessagesAsync(userId, Ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task SubscribedAt_StampedOnFirstOptIn_KeptAcrossReOptInAndNoOps()
    {
        var userId = Guid.NewGuid();
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Ct);
        var first = (await RowsAsync(userId)).Single().SubscribedAt;
        first.Should().NotBeNull();

        _clock.Advance(Duration.FromMinutes(1));
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Ct);
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: true, source: "Profile", cancellationToken: Ct);
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, inboxEnabled: true, source: "Profile", cancellationToken: Ct);

        (await RowsAsync(userId)).Single().SubscribedAt.Should().Be(first);
    }

    [HumansFact]
    public async Task SubscribedAt_StampedWhenAnOptedOutRowWithoutOneOptsIn()
    {
        var userId = Guid.NewGuid();
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: true, source: "Default");

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, inboxEnabled: true, source: "Profile", cancellationToken: Ct);

        (await RowsAsync(userId)).Single().SubscribedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task CountByCategoryAndState_CountsRowsAndUnshadowedLegacyRows()
    {
        for (var i = 0; i < 3; i++)
            await SeedRowAsync(Guid.NewGuid(), MessageCategory.Marketing, optedOut: false);
        await SeedRowAsync(Guid.NewGuid(), MessageCategory.Marketing, optedOut: true);
        SeedLegacy(Guid.NewGuid(), LegacyMessageCategory.Marketing, optedOut: true);
        var shadowed = Guid.NewGuid();
        SeedLegacy(shadowed, LegacyMessageCategory.Marketing, optedOut: true);
        await SeedRowAsync(shadowed, MessageCategory.Marketing, optedOut: null);

        (await _service.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: false, Ct)).Should().Be(3);
        (await _service.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: true, Ct)).Should().Be(2);
    }

    [HumansFact]
    public void UnsubscribeToken_RoundTrips()
    {
        var userId = Guid.NewGuid();
        var url = _service.GenerateBrowserUnsubscribeUrl(userId, MessageCategory.TeamUpdates);
        var token = new Uri(url).Segments[^1];

        _service.ValidateUnsubscribeToken(token).Should().Be((TokenValidationStatus.Valid, userId, MessageCategory.TeamUpdates));
    }

    [HumansFact]
    public void UnsubscribeToken_MintedBeforeTheMove_StillValidates()
    {
        // Users minted tokens with this protector purpose and "{userId}|{category}" payload; links already
        // in members' inboxes must keep working after the move to Email.
        var userId = Guid.NewGuid();
        var legacyToken = _dataProtection.CreateProtector("CommunicationPreference").ToTimeLimitedDataProtector()
            .Protect($"{userId}|Marketing", TimeSpan.FromDays(90));

        _service.ValidateUnsubscribeToken(legacyToken).Should().Be((TokenValidationStatus.Valid, userId, MessageCategory.Marketing));
    }

    [HumansTheory]
    [InlineData("this-is-not-a-valid-token")]
    [InlineData("aGVsbG8gd29ybGQ=")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("%")]
    [InlineData("a")]
    public void UnsubscribeToken_Tampered_IsInvalid(string? token) =>
        _service.ValidateUnsubscribeToken(token!).Should().Be((TokenValidationStatus.Invalid, Guid.Empty, default(MessageCategory)));

    [HumansFact]
    public void UnsubscribeHeaders_KeepTheUnsubscribeUrls()
    {
        var headers = _service.GenerateUnsubscribeHeaders(Guid.NewGuid(), MessageCategory.Marketing);

        headers["List-Unsubscribe"].Should().StartWith("<https://test.example.com/Email/Unsubscribe/OneClick?token=");
        headers["List-Unsubscribe-Post"].Should().Be("List-Unsubscribe=One-Click");
        _service.GenerateBrowserUnsubscribeUrl(Guid.NewGuid(), MessageCategory.Marketing)
            .Should().StartWith("https://test.example.com/Email/Unsubscribe/");
    }

    [HumansFact]
    public async Task Reads_FallBackToTheLegacyRow_UntilARowShadowsIt()
    {
        var userId = Guid.NewGuid();
        SeedLegacy(userId, LegacyMessageCategory.Marketing, optedOut: false, inboxEnabled: false, source: "MailerLiteSync");
        SeedLegacy(userId, LegacyMessageCategory.VolunteerUpdates, optedOut: true);

        (await _service.IsOptedOutAsync(userId, MessageCategory.Marketing, Ct)).Should().BeFalse();
        (await _service.GetPreferenceOrNullAsync(userId, MessageCategory.Marketing, Ct))!.UpdateSource.Should().Be("MailerLiteSync");
        (await _service.GetPreferencesReadOnlyAsync(userId, Ct)).Select(p => p.Category)
            .Should().BeEquivalentTo([MessageCategory.Marketing, MessageCategory.VolunteerUpdates]);
        (await _service.GetUsersWithInboxDisabledAsync([userId], MessageCategory.Marketing, Ct)).Should().Contain(userId);
        (await _service.GetPreferencesForCategoryAsync(MessageCategory.Marketing, Ct))[userId].OptedOut.Should().BeFalse();

        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: true);

        (await _service.IsOptedOutAsync(userId, MessageCategory.Marketing, Ct)).Should().BeTrue();
        (await _service.GetUsersWithInboxDisabledAsync([userId], MessageCategory.Marketing, Ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Reads_ResolveAMergedIdToItsSurvivor()
    {
        var survivor = Guid.NewGuid();
        var merged = Guid.NewGuid();
        _mergedTo[merged] = survivor;
        await SeedRowAsync(survivor, MessageCategory.FacilitatedMessages, optedOut: true);

        (await _service.AcceptsFacilitatedMessagesAsync(merged, Ct)).Should().BeFalse();
    }

    [HumansFact]
    public async Task Write_SeedsFromTheLegacyRow()
    {
        var userId = Guid.NewGuid();
        var subscribed = _clock.GetCurrentInstant() - Duration.FromDays(30);
        SeedLegacy(userId, LegacyMessageCategory.Marketing, optedOut: false, inboxEnabled: false, subscribedAt: subscribed);

        // Same value as the legacy row: nothing written, nothing audited.
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "MailerLiteSync", cancellationToken: Ct);
        (await RowsAsync(userId)).Should().BeEmpty();
        _audit.ReceivedCalls().Should().BeEmpty();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: true, source: "OneClick", cancellationToken: Ct);

        var row = (await RowsAsync(userId)).Should().ContainSingle().Subject;
        row.OptedOut.Should().BeTrue();
        row.InboxEnabled.Should().BeFalse();
        row.SubscribedAt.Should().Be(subscribed);
        row.UpdateSource.Should().Be("OneClick");
    }

    [HumansFact]
    public async Task Reset_OverALegacyRow_LeavesAClearedRowThatShadowsIt()
    {
        var userId = Guid.NewGuid();
        SeedLegacy(userId, LegacyMessageCategory.Marketing, optedOut: false, source: "MailerLiteSync");

        await _service.ResetPreferenceAsync(userId, MessageCategory.Marketing, "MailerLiteSyncReset", Ct);

        (await RowsAsync(userId)).Should().ContainSingle().Which.OptedOut.Should().BeNull();
        (await _service.GetPreferenceOrNullAsync(userId, MessageCategory.Marketing, Ct)).Should().BeNull();
        (await _service.GetPreferencesForCategoryAsync(MessageCategory.Marketing, Ct)).Should().NotContainKey(userId);
        (await _service.IsOptedOutAsync(userId, MessageCategory.Marketing, Ct)).Should().BeTrue();
        (await _service.GetUncopiedLegacyPreferencesAsync(Ct)).Should().BeEmpty();
        _audit.ReceivedCalls().Should().ContainSingle();

        // Opting in again replaces the cleared row with a fresh preference.
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Ct);
        (await RowsAsync(userId)).Should().ContainSingle().Which.OptedOut.Should().BeFalse();
    }

    [HumansFact]
    public async Task Reset_WithoutALegacyRow_DeletesTheRow_AndIsANoOpWhenNothingIsRecorded()
    {
        var userId = Guid.NewGuid();
        await _service.ResetPreferenceAsync(userId, MessageCategory.Marketing, "Reset", Ct);
        _audit.ReceivedCalls().Should().BeEmpty();

        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: false);
        await _service.ResetPreferenceAsync(userId, MessageCategory.Marketing, "Reset", Ct);

        (await RowsAsync(userId)).Should().BeEmpty();
        _audit.ReceivedCalls().Should().ContainSingle();
    }

    [HumansFact]
    public async Task Copy_WritesEveryUnshadowedLegacyRow_Once_AndNeverOverwritesALaterRow()
    {
        var copied = Guid.NewGuid();
        var subscribed = _clock.GetCurrentInstant() - Duration.FromDays(3);
        SeedLegacy(copied, LegacyMessageCategory.Marketing, optedOut: false, inboxEnabled: false, source: "MailerLiteSync", subscribedAt: subscribed);
        var written = Guid.NewGuid();
        SeedLegacy(written, LegacyMessageCategory.Marketing, optedOut: false);
        await SeedRowAsync(written, MessageCategory.Marketing, optedOut: true, source: "OneClick");

        var pending = await _service.GetUncopiedLegacyPreferencesAsync(Ct);
        pending.Should().ContainSingle().Which.UserId.Should().Be(copied);

        (await _service.CopyLegacyPreferencesAsync(Ct)).Should().Be(1);
        (await _service.CopyLegacyPreferencesAsync(Ct)).Should().Be(0);
        (await _service.GetUncopiedLegacyPreferencesAsync(Ct)).Should().BeEmpty();

        var row = (await RowsAsync(copied)).Single();
        (row.Category, row.OptedOut, row.InboxEnabled, row.UpdateSource, row.SubscribedAt)
            .Should().Be((MessageCategory.Marketing, false, false, "MailerLiteSync", subscribed));
        (await RowsAsync(written)).Single().UpdateSource.Should().Be("OneClick");
    }

    [HumansFact]
    public void DataMigration_IsRegisteredUnderItsKey() =>
        new SectionDataMigrations().Migrations().Select(m => m.Key)
            .Should().ContainSingle().Which.Should().Be("email.communication-preferences");

    [HumansFact]
    public async Task Merge_LaterPreferenceWins_SourceOnlyMoves_AndSourceLegacyIsShadowed()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var early = _clock.GetCurrentInstant() - Duration.FromDays(2);
        var late = _clock.GetCurrentInstant() - Duration.FromDays(1);
        // Marketing: target's legacy row is older than the source's live row → source wins.
        SeedLegacy(target, LegacyMessageCategory.Marketing, optedOut: true, updatedAt: early);
        await SeedRowAsync(source, MessageCategory.Marketing, optedOut: false, updatedAt: late, source: "Profile");
        // TeamUpdates: only the source has it, through its legacy row → moves to target.
        SeedLegacy(source, LegacyMessageCategory.TeamUpdates, optedOut: true, inboxEnabled: false);
        var now = _clock.GetCurrentInstant();

        await _service.ReassignAsync(source, target, Guid.NewGuid(), now, Ct);

        var targetRows = (await RowsAsync(target)).ToDictionary(r => r.Category);
        targetRows[MessageCategory.Marketing].OptedOut.Should().BeFalse();
        targetRows[MessageCategory.Marketing].UpdatedAt.Should().Be(now);
        targetRows[MessageCategory.TeamUpdates].OptedOut.Should().BeTrue();
        targetRows[MessageCategory.TeamUpdates].InboxEnabled.Should().BeFalse();
        (await RowsAsync(source)).Should().ContainSingle()
            .Which.Should().Match<CommunicationPreference>(r => r.Category == MessageCategory.TeamUpdates && r.OptedOut == null);
        (await _service.GetUncopiedLegacyPreferencesAsync(Ct)).Select(u => u.UserId).Should().OnlyContain(id => id == target);
    }

    [HumansFact]
    public async Task Merge_TargetKeepsItsNewerPreference()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await SeedRowAsync(source, MessageCategory.Marketing, optedOut: false, updatedAt: _clock.GetCurrentInstant() - Duration.FromDays(2));
        await SeedRowAsync(target, MessageCategory.Marketing, optedOut: true, updatedAt: _clock.GetCurrentInstant() - Duration.FromDays(1));

        await _service.ReassignAsync(source, target, Guid.NewGuid(), _clock.GetCurrentInstant(), Ct);

        (await RowsAsync(target)).Single().OptedOut.Should().BeTrue();
        (await RowsAsync(source)).Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Merge_KeepsKnownSubscribedAt_IndependentOfWinner(bool targetHasTimestamp, bool sourceIsNewer)
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var sourceSubscribedAt = _clock.GetCurrentInstant() - Duration.FromDays(30);
        Instant? targetSubscribedAt = targetHasTimestamp ? _clock.GetCurrentInstant() - Duration.FromDays(15) : null;
        await SeedRowAsync(source, MessageCategory.Marketing, optedOut: true,
            updatedAt: _clock.GetCurrentInstant() - Duration.FromDays(sourceIsNewer ? 1 : 3), subscribedAt: sourceSubscribedAt);
        await SeedRowAsync(target, MessageCategory.Marketing, optedOut: false,
            updatedAt: _clock.GetCurrentInstant() - Duration.FromDays(2), subscribedAt: targetSubscribedAt);

        await _service.ReassignAsync(source, target, Guid.NewGuid(), _clock.GetCurrentInstant(), Ct);
        await _service.ReassignAsync(source, target, Guid.NewGuid(), _clock.GetCurrentInstant(), Ct);

        var row = (await RowsAsync(target)).Should().ContainSingle().Subject;
        row.SubscribedAt.Should().Be(targetSubscribedAt ?? sourceSubscribedAt);
        row.OptedOut.Should().Be(sourceIsNewer);
    }

    [HumansFact]
    public async Task Export_CoversRowsAndLegacy_AndEraseDeletesTheRows()
    {
        var userId = Guid.NewGuid();
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: false);
        SeedLegacy(userId, LegacyMessageCategory.TeamUpdates, optedOut: true);

        var slice = (await _service.ContributeForUserAsync(userId, Ct)).Should().ContainSingle().Subject;
        slice.SectionName.Should().Be("CommunicationPreferences");
        ((System.Collections.IList)slice.Data!).Count.Should().Be(2);
        _service.ErasureDeclaration.Should().ContainKey("CommunicationPreferences").WhoseValue.Should().BeNull();

        await _service.EraseForUserAsync(userId, Ct);

        (await RowsAsync(userId)).Should().BeEmpty();
    }
}
