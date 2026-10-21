using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Configuration;
using Humans.Email.Contracts;
using Humans.Email.Data;
using Humans.Email.Domain;
using Humans.Email.Services;
using Humans.Gdpr.Contracts;
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

/// <summary>
/// <see cref="CommunicationPreferenceService"/> over <c>email_communication_preferences</c> (Users
/// decomposition commit 14): the rules carried over from Users, cleared rows left from before the
/// <c>email.communication-preferences</c> copy was retired, the merge fold and the GDPR slice.
/// </summary>
public sealed class CommunicationPreferenceServiceTests : IDisposable
{
    private readonly EmailDbContext _db;
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 4, 1, 12, 0));
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly Dictionary<Guid, Guid> _mergedTo = [];
    private readonly IDataProtectionProvider _dataProtection = DataProtectionProvider.Create("TestApp");
    private readonly CommunicationPreferenceService _service;

    public CommunicationPreferenceServiceTests()
    {
        var options = new DbContextOptionsBuilder<EmailDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _db = new EmailDbContext(options);

        // The resolving read follows a merge to its survivor.
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var id = ci.Arg<Guid>();
                return new ValueTask<UserInfo?>(Info(_mergedTo.GetValueOrDefault(id, id)));
            });

        var tokenProvider = new UnsubscribeTokenProvider(
            _dataProtection, Options.Create(new EmailSettings { BaseUrl = "https://test.example.com" }),
            NullLogger<UnsubscribeTokenProvider>.Instance);

        _service = new CommunicationPreferenceService(
            new CommunicationPreferenceRepository(new TestDbContextFactory<EmailDbContext>(options)),
            _users, tokenProvider, _clock, _audit, NullLogger<CommunicationPreferenceService>.Instance);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UserInfo Info(Guid id) => UserInfo.Create(
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

    // ─── Rules carried over from Users ───────────────────────────────────────────

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
    public async Task CountByCategoryAndState_CountsRecordedRows_SkippingClearedOnes()
    {
        for (var i = 0; i < 3; i++)
            await SeedRowAsync(Guid.NewGuid(), MessageCategory.Marketing, optedOut: false);
        await SeedRowAsync(Guid.NewGuid(), MessageCategory.Marketing, optedOut: true);
        await SeedRowAsync(Guid.NewGuid(), MessageCategory.Marketing, optedOut: null);

        (await _service.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: false, Ct)).Should().Be(3);
        (await _service.GetCountByCategoryAndStateAsync(MessageCategory.Marketing, optedOut: true, Ct)).Should().Be(1);
    }

    // ─── Unsubscribe tokens ──────────────────────────────────────────────────────

    [HumansFact]
    public void UnsubscribeToken_RoundTrips()
    {
        var userId = Guid.NewGuid();
        var token = _service.GenerateUnsubscribeToken(userId, MessageCategory.TeamUpdates);

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
    public void UnsubscribeToken_Tampered_IsInvalid(string token) =>
        _service.ValidateUnsubscribeToken(token).Should().Be((TokenValidationStatus.Invalid, Guid.Empty, default(MessageCategory)));

    [HumansFact]
    public void UnsubscribeHeaders_KeepTheUnsubscribeUrls()
    {
        var headers = _service.GenerateUnsubscribeHeaders(Guid.NewGuid(), MessageCategory.Marketing);

        headers["List-Unsubscribe"].Should().StartWith("<https://test.example.com/Email/Unsubscribe/OneClick?token=");
        headers["List-Unsubscribe-Post"].Should().Be("List-Unsubscribe=One-Click");
        _service.GenerateBrowserUnsubscribeUrl(Guid.NewGuid(), MessageCategory.Marketing)
            .Should().StartWith("https://test.example.com/Email/Unsubscribe/");
    }

    // ─── Reads, writes, cleared rows ─────────────────────────────────────────────

    [HumansFact]
    public async Task Reads_SkipAClearedRow()
    {
        var userId = Guid.NewGuid();
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: null, inboxEnabled: false);
        await SeedRowAsync(userId, MessageCategory.VolunteerUpdates, optedOut: true, inboxEnabled: false);

        (await _service.GetPreferenceOrNullAsync(userId, MessageCategory.Marketing, Ct)).Should().BeNull();
        (await _service.IsOptedOutAsync(userId, MessageCategory.Marketing, Ct)).Should().BeTrue();
        (await _service.GetPreferencesReadOnlyAsync(userId, Ct)).Select(p => p.Category)
            .Should().Equal(MessageCategory.VolunteerUpdates);
        (await _service.GetUsersWithInboxDisabledAsync([userId], MessageCategory.Marketing, Ct)).Should().BeEmpty();
        (await _service.GetPreferencesForCategoryAsync(MessageCategory.Marketing, Ct)).Should().NotContainKey(userId);
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
    public async Task Write_StartsFromTheRecordedRow()
    {
        var userId = Guid.NewGuid();
        var subscribed = _clock.GetCurrentInstant() - Duration.FromDays(30);
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: false, inboxEnabled: false,
            source: "MailerLiteSync", subscribedAt: subscribed);

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: true, source: "OneClick", cancellationToken: Ct);

        var row = (await RowsAsync(userId)).Should().ContainSingle().Subject;
        row.OptedOut.Should().BeTrue();
        row.InboxEnabled.Should().BeFalse();
        row.SubscribedAt.Should().Be(subscribed);
        row.UpdateSource.Should().Be("OneClick");
    }

    [HumansFact]
    public async Task Reset_DeletesTheRow_AndIsANoOpWhenNothingIsRecorded()
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
    public async Task ClearedRow_ReadsAsNothingRecorded_AndAnOptInReplacesIt()
    {
        var userId = Guid.NewGuid();
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: null);

        await _service.ResetPreferenceAsync(userId, MessageCategory.Marketing, "Reset", Ct);
        _audit.ReceivedCalls().Should().BeEmpty();

        await _service.UpdatePreferenceAsync(userId, MessageCategory.Marketing, optedOut: false, source: "Profile", cancellationToken: Ct);
        var row = (await RowsAsync(userId)).Should().ContainSingle().Subject;
        row.OptedOut.Should().BeFalse();
        row.SubscribedAt.Should().Be(_clock.GetCurrentInstant());
    }

    // ─── Merge fold ──────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Merge_LaterPreferenceWins_SourceOnlyMoves_AndClearedRowsAreDropped()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        var early = _clock.GetCurrentInstant() - Duration.FromDays(2);
        var late = _clock.GetCurrentInstant() - Duration.FromDays(1);
        // Marketing: target's row is older than the source's → source wins.
        await SeedRowAsync(target, MessageCategory.Marketing, optedOut: true, updatedAt: early);
        await SeedRowAsync(source, MessageCategory.Marketing, optedOut: false, updatedAt: late, source: "Profile");
        // TeamUpdates: only the source has it → moves to target.
        await SeedRowAsync(source, MessageCategory.TeamUpdates, optedOut: true, inboxEnabled: false);
        await SeedRowAsync(source, MessageCategory.Governance, optedOut: null);
        await SeedRowAsync(target, MessageCategory.VolunteerUpdates, optedOut: null);
        var now = _clock.GetCurrentInstant();

        await _service.ReassignAsync(source, target, Guid.NewGuid(), now, Ct);

        var targetRows = (await RowsAsync(target)).ToDictionary(r => r.Category);
        targetRows.Keys.Should().BeEquivalentTo([MessageCategory.Marketing, MessageCategory.TeamUpdates]);
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

    // ─── GDPR ────────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Export_CoversRecordedRows_AndEraseDeletesEveryRow()
    {
        var userId = Guid.NewGuid();
        await SeedRowAsync(userId, MessageCategory.Marketing, optedOut: false);
        await SeedRowAsync(userId, MessageCategory.TeamUpdates, optedOut: null);

        var slice = (await _service.ContributeForUserAsync(userId, Ct)).Should().ContainSingle().Subject;
        slice.SectionName.Should().Be("CommunicationPreferences");
        ((System.Collections.IList)slice.Data!).Count.Should().Be(1);
        _service.ErasureDeclaration.Should().ContainKey("CommunicationPreferences").WhoseValue.Should().BeNull();

        await _service.EraseForUserAsync(userId, Ct);

        (await RowsAsync(userId)).Should().BeEmpty();
    }
}
