using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Email.Services;
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
    private readonly ICommunicationPreferenceLock _lock = Substitute.For<ICommunicationPreferenceLock>();
    private readonly Dictionary<Guid, Guid> _mergedTo = [];
    private readonly IDataProtectionProvider _dataProtection = DataProtectionProvider.Create("TestApp");
    private readonly CommunicationPreferenceService _service;

    public CommunicationPreferenceServiceTests()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new EmailDbContext(options);

        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<Guid>();
                return new ValueTask<UserInfo?>(Info(_mergedTo.GetValueOrDefault(id, id)));
            });
        _lock.GetLockedCategoriesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<MessageCategory>>([]));

        var tokenProvider = new UnsubscribeTokenProvider(
            _dataProtection, Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" }),
            NullLogger<UnsubscribeTokenProvider>.Instance);

        _service = new CommunicationPreferenceService(
            new CommunicationPreferenceRepository(new TestDbContextFactory<EmailDbContext>(options)),
            _users, tokenProvider, [_lock], _clock, _audit, NullLogger<CommunicationPreferenceService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static UserInfo Info(Guid id) => UserInfo.Create(
        new User { Id = id, DisplayName = "", PreferredLanguage = "en", CreatedAt = Instant.MinValue },
        [], [], profile: null);

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
        audit[0].Should().Be(EmailAuditActions.CommunicationPreferenceChanged);
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
    public async Task MemberToggle_ALockedCategoryIsRefused(bool locked)
    {
        var userId = Guid.NewGuid();
        LockTicketing(userId, locked);

        var applied = await _service.UpdatePreferenceAsync(userId, MessageCategory.Ticketing,
            optedOut: true, inboxEnabled: false, source: "Guest", actorUserId: userId, cancellationToken: Ct);

        applied.Should().Be(!locked);
        (await RowsAsync(userId)).Should().HaveCount(locked ? 0 : 1);
        _audit.ReceivedCalls().Should().HaveCount(locked ? 0 : 1);
    }

    [HumansFact]
    public async Task MemberToggle_OtherCategoriesIgnoreTheLock()
    {
        var userId = Guid.NewGuid();
        LockTicketing(userId, locked: true);

        (await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing,
            optedOut: true, inboxEnabled: false, source: "Guest", actorUserId: userId, cancellationToken: Ct))
            .Should().BeTrue();
    }

    private void LockTicketing(Guid userId, bool locked) =>
        _lock.GetLockedCategoriesAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<MessageCategory>>(locked ? [MessageCategory.Ticketing] : []));

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
    public async Task CountByCategoryAndState_CountsRecordedRows_AndIgnoresHistoricalClears()
    {
        for (var i = 0; i < 3; i++)
            await SeedRowAsync(Guid.NewGuid(), MessageCategory.Marketing, optedOut: false);
        await SeedRowAsync(Guid.NewGuid(), MessageCategory.Marketing, optedOut: true);
        await SeedRowAsync(Guid.NewGuid(), MessageCategory.Marketing, optedOut: true);
        var shadowed = Guid.NewGuid();
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
    public async Task Reads_UseEmailRows_ForEachCategory()
    {
        var userId = Guid.NewGuid();
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: false, inboxEnabled: false, source: "MailerLiteSync");
        await SeedRowAsync(userId, MessageCategory.VolunteerUpdates, optedOut: true);

        (await _service.IsOptedOutAsync(userId, MessageCategory.Marketing, Ct)).Should().BeFalse();
        (await _service.GetPreferenceOrNullAsync(userId, MessageCategory.Marketing, Ct))!.UpdateSource.Should().Be("MailerLiteSync");
        (await _service.GetPreferencesReadOnlyAsync(userId, Ct)).Select(p => p.Category)
            .Should().BeEquivalentTo([MessageCategory.Marketing, MessageCategory.VolunteerUpdates]);
        (await _service.GetUsersWithInboxDisabledAsync([userId], MessageCategory.Marketing, Ct)).Should().Contain(userId);
        (await _service.GetPreferencesForCategoryAsync(MessageCategory.Marketing, Ct))[userId].OptedOut.Should().BeFalse();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: true,
            inboxEnabled: true, source: "Profile", cancellationToken: Ct);

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
    public async Task Write_PreservesStoredInboxAndSubscriptionMetadata()
    {
        var userId = Guid.NewGuid();
        var subscribed = _clock.GetCurrentInstant() - Duration.FromDays(30);
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: false, inboxEnabled: false, subscribedAt: subscribed);

        // An unchanged preference keeps its metadata and produces no audit.
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "MailerLiteSync", cancellationToken: Ct);
        (await RowsAsync(userId)).Should().ContainSingle();
        _audit.ReceivedCalls().Should().BeEmpty();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: true, source: "OneClick", cancellationToken: Ct);

        var row = (await RowsAsync(userId)).Should().ContainSingle().Subject;
        row.OptedOut.Should().BeTrue();
        row.InboxEnabled.Should().BeFalse();
        row.SubscribedAt.Should().Be(subscribed);
        row.UpdateSource.Should().Be("OneClick");
    }

    [HumansFact]
    public async Task Reset_DeletesThePreference_AndUsesTheCategoryDefault()
    {
        var userId = Guid.NewGuid();
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: false, source: "MailerLiteSync");

        await _service.ResetPreferenceAsync(userId, MessageCategory.Marketing, "MailerLiteSyncReset", Ct);

        (await RowsAsync(userId)).Should().BeEmpty();
        (await _service.GetPreferenceOrNullAsync(userId, MessageCategory.Marketing, Ct)).Should().BeNull();
        (await _service.GetPreferencesForCategoryAsync(MessageCategory.Marketing, Ct)).Should().NotContainKey(userId);
        (await _service.IsOptedOutAsync(userId, MessageCategory.Marketing, Ct)).Should().BeTrue();
        _audit.ReceivedCalls().Should().ContainSingle();

        // Opting in again records a fresh preference.
        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Ct);
        (await RowsAsync(userId)).Should().ContainSingle().Which.OptedOut.Should().BeFalse();
    }

    [HumansFact]
    public async Task HistoricalClearedRows_UseDefaults_AndDoNotDisableInbox()
    {
        var userId = Guid.NewGuid();
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: null, inboxEnabled: false);

        (await _service.GetPreferencesReadOnlyAsync(userId, Ct)).Should().BeEmpty();
        (await _service.GetPreferencesForCategoryAsync(MessageCategory.Marketing, Ct)).Should().BeEmpty();
        (await _service.GetUsersWithInboxDisabledAsync([userId], MessageCategory.Marketing, Ct)).Should().BeEmpty();
        (await _service.IsOptedOutAsync(userId, MessageCategory.Marketing, Ct)).Should().BeTrue();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false,
            source: "Profile", cancellationToken: Ct);

        var row = (await RowsAsync(userId)).Should().ContainSingle().Subject;
        row.OptedOut.Should().BeFalse();
        row.InboxEnabled.Should().BeTrue();
        row.SubscribedAt.Should().Be(_clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task Reset_IsANoOpWhenNothingIsRecorded_AndDeletesAnExistingRow()
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
    public async Task Merge_LaterPreferenceWins_SourceOnlyMoves_AndSourceIsDeleted()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var early = _clock.GetCurrentInstant() - Duration.FromDays(2);
        var late = _clock.GetCurrentInstant() - Duration.FromDays(1);
        await SeedRowAsync(target, MessageCategory.Marketing, optedOut: true, updatedAt: early);
        await SeedRowAsync(source, MessageCategory.Marketing, optedOut: false, updatedAt: late, source: "Profile");
        await SeedRowAsync(source, MessageCategory.TeamUpdates, optedOut: true, inboxEnabled: false);
        var now = _clock.GetCurrentInstant();

        await _service.ReassignAsync(source, target, Guid.NewGuid(), now, Ct);

        var targetRows = (await RowsAsync(target)).ToDictionary(r => r.Category);
        targetRows[MessageCategory.Marketing].OptedOut.Should().BeFalse();
        targetRows[MessageCategory.Marketing].UpdatedAt.Should().Be(now);
        targetRows[MessageCategory.TeamUpdates].OptedOut.Should().BeTrue();
        targetRows[MessageCategory.TeamUpdates].InboxEnabled.Should().BeFalse();
        (await RowsAsync(source)).Should().BeEmpty();
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

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_CoversEmailRows_AndEraseDeletesTheRows(bool hasSubscribedAt)
    {
        var userId = Guid.NewGuid();
        var subscribed = hasSubscribedAt ? _clock.GetCurrentInstant() : (Instant?)null;
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: false, subscribedAt: subscribed);
        await SeedRowAsync(userId, MessageCategory.TeamUpdates, optedOut: true);

        var slice = (await _service.ContributeForUserAsync(userId, Ct)).Should().ContainSingle().Subject;
        slice.SectionName.Should().Be("CommunicationPreferences");
        ((System.Collections.IList)slice.Data!).Count.Should().Be(2);
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(slice.Data));
        json.RootElement[0].GetProperty("SubscribedAt").GetString()
            .Should().Be(hasSubscribedAt ? "2026-04-01T12:00:00Z" : null);
        _service.ErasureDeclaration.Should().ContainKey("CommunicationPreferences").WhoseValue.Should().BeNull();

        await _service.EraseForUserAsync(userId, Ct);

        (await RowsAsync(userId)).Should().BeEmpty();
    }
}
