using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Consent.Data;
using Humans.Consent.Domain;
using Humans.Consent.Services;
using Humans.Consent.ViewComponents;
using Humans.Onboarding.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Consent.Tests.Services;

public sealed class ConsentReviewServiceTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 21, 12, 0);
    private static readonly Instant Earlier = Instant.FromUtc(2026, 5, 1, 9, 0);

    private readonly FakeClock _clock = new(Now);
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IUserWrite _userWrite = Substitute.For<IUserWrite>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly List<UserInfo> _all = [];
    private readonly LegalDbContext _db;
    private readonly ConsentReviewService _sut;

    public ConsentReviewServiceTests()
    {
        var factory = new TestDbContextFactory<LegalDbContext>(
            new DbContextOptionsBuilder<LegalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db = factory.CreateDbContext();
        _sut = new ConsentReviewService(new ConsentReviewRepository(factory), _users, _userWrite, _audit, _clock);
        _users.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>()).Returns(_ => _all.ToList());
        _userWrite.SetRejectedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new OnboardingResult(true));
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A named member whose frozen profile columns hold <paramref name="profile"/>'s review.</summary>
    private UserInfo SeedUser(
        ProfileInfo? profile = null, UserState state = UserState.Active, Guid? id = null, Instant? mergedAt = null)
    {
        var userId = id ?? Guid.NewGuid();
        var info = UserInfo.Create(
            new User { Id = userId, BurnerName = "Ana", CreatedAt = Now, State = state, MergedAt = mergedAt },
            [], [], [],
            profile ?? UserFixtures.Profile(burnerName: "Ana", firstName: "Ana", lastName: "García"),
            []);
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
        _users.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
        _all.Add(info);
        return info;
    }

    private static ProfileInfo LegacyProfile(
        LegacyConsentCheckStatus? status = null, bool isApproved = false, Instant? rejectedAt = null,
        string? notes = null, string? reason = null) =>
        UserFixtures.Profile(
            burnerName: "Ana", firstName: "Ana", lastName: "García",
            isApproved: isApproved, consentCheckStatus: status, consentCheckAt: status is null ? null : Earlier,
            consentCheckNotes: notes, rejectionReason: reason, rejectedAt: rejectedAt,
            rejectedByUserId: rejectedAt is null ? null : Guid.NewGuid());

    private async Task SeedRowAsync(ConsentReview row)
    {
        _db.ConsentReviews.Add(row);
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
    }

    private Task<ConsentReview?> RowAsync(Guid userId) =>
        _db.ConsentReviews.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId, Ct);

    [HumansFact]
    public async Task Read_FallsBackToTheLegacyProfileReview_UntilARowExists()
    {
        var user = SeedUser(LegacyProfile(LegacyConsentCheckStatus.Cleared, isApproved: true, notes: "ok"));

        var legacy = await _sut.GetReviewAsync(user.Id, Ct);
        legacy.Status.Should().Be(ConsentCheckStatus.Cleared);
        legacy.IsApproved.Should().BeTrue();
        legacy.Notes.Should().Be("ok");
        (await _sut.GetReviewsAsync(Ct))[user.Id].Should().Be(legacy);

        await SeedRowAsync(new ConsentReview { UserId = user.Id, Status = ConsentCheckStatus.Flagged, UpdatedAt = Now });

        var row = await _sut.GetReviewAsync(user.Id, Ct);
        row.Status.Should().Be(ConsentCheckStatus.Flagged);
        row.IsApproved.Should().BeFalse();
        (await _sut.GetReviewsAsync(Ct))[user.Id].Should().Be(row);
    }

    [HumansFact]
    public async Task Read_NoReviewAnywhere_IsNone_AndNotListed()
    {
        var user = SeedUser();

        (await _sut.GetReviewAsync(user.Id, Ct)).Should().Be(ConsentReviewInfo.None(user.Id));
        (await _sut.GetReviewsAsync(Ct)).Should().NotContainKey(user.Id);
    }

    [HumansFact]
    public async Task Read_AMergedAwayId_ResolvesToTheSurvivorsReview()
    {
        var survivor = SeedUser();
        var archivedId = Guid.NewGuid();
        _users.GetUserInfoAsync(archivedId, Arg.Any<CancellationToken>()).Returns(survivor);
        await SeedRowAsync(new ConsentReview { UserId = survivor.Id, IsApproved = true, UpdatedAt = Now });

        (await _sut.GetReviewAsync(archivedId, Ct)).Should().BeEquivalentTo(
            new { UserId = survivor.Id, IsApproved = true }, o => o.ExcludingMissingMembers());
    }

    [HumansFact]
    public async Task LegacyRejectedProfile_WithNonRejectedState_StillReadsRejectedEverywhere()
    {
        // A legacy profile rejection whose users.State never recorded Rejected: every
        // reader that gated on profiles.RejectedAt must still see the rejection.
        var user = SeedUser(LegacyProfile(LegacyConsentCheckStatus.Flagged, rejectedAt: Earlier, reason: "dup"));
        user.State.Should().NotBe(UserState.Rejected);

        user.IsRejected.Should().BeTrue();
        var review = await _sut.GetReviewAsync(user.Id, Ct);
        review.IsRejected.Should().BeTrue();
        review.RejectionReason.Should().Be("dup");
        (await _sut.GetReviewsAsync(Ct))[user.Id].IsRejected.Should().BeTrue();
        user.IsConsentCheckFlagged(review).Should().BeFalse();
        (await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Cleared, Guid.NewGuid(), null, Ct))
            .ErrorKey.Should().Be("AlreadyRejected");
        (await _sut.RecordRejectionAsync(user.Id, Guid.NewGuid(), "again", Ct))
            .ErrorKey.Should().Be("AlreadyRejected");

        // The copy records the rejection as the stored state as well as copying the row.
        var pending = (await _sut.GetUncopiedLegacyReviewsAsync(Ct)).Should().ContainSingle().Subject;
        pending.Should().BeEquivalentTo(new { CopyRow = true, SetRejectedState = true }, o => o.ExcludingMissingMembers());
        await _sut.CopyLegacyReviewsAsync(Ct);
        await _userWrite.Received(1).SetRejectedAsync(user.Id, Arg.Any<CancellationToken>());
        (await RowAsync(user.Id))!.RejectedAt.Should().Be(Earlier);
    }

    [HumansFact]
    public async Task RecordConsentCheck_Cleared_ApprovesAndStamps()
    {
        var user = SeedUser();
        var reviewer = Guid.NewGuid();

        (await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Cleared, reviewer, "Looks good", Ct))
            .Success.Should().BeTrue();

        (await RowAsync(user.Id)).Should().BeEquivalentTo(new
        {
            Status = ConsentCheckStatus.Cleared,
            IsApproved = true,
            CheckedAt = Now,
            CheckedByUserId = reviewer,
            Notes = "Looks good",
        }, o => o.ExcludingMissingMembers());
        await _audit.Received(1).LogAsync(
            ConsentAuditActions.ConsentCheckCleared, "Profile", user.Id, "Consent check cleared", reviewer);
    }

    [HumansFact]
    public async Task RecordConsentCheck_Flagged_ClearsApproval()
    {
        var user = SeedUser(LegacyProfile(LegacyConsentCheckStatus.Cleared, isApproved: true));

        var reviewer = Guid.NewGuid();
        (await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Flagged, reviewer, "Concern", Ct))
            .Success.Should().BeTrue();

        var row = await RowAsync(user.Id);
        row!.Status.Should().Be(ConsentCheckStatus.Flagged);
        row.IsApproved.Should().BeFalse();
        await _audit.Received(1).LogAsync(
            ConsentAuditActions.ConsentCheckFlagged, "Profile", user.Id, "Consent check flagged: Concern", reviewer);
    }

    [HumansFact]
    public async Task RecordConsentCheck_RefusedWrite_AuditsNothing()
    {
        var user = SeedUser();
        (await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Flagged, Guid.NewGuid(), null, Ct)).Success.Should().BeTrue();
        _audit.ClearReceivedCalls();

        // A flagged-then-rejected member cannot be cleared; the refusal leaves no audit row.
        (await _sut.RecordRejectionAsync(user.Id, Guid.NewGuid(), "dup", Ct)).Success.Should().BeTrue();
        _audit.ClearReceivedCalls();
        (await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Cleared, Guid.NewGuid(), null, Ct))
            .Success.Should().BeFalse();

        await _audit.DidNotReceiveWithAnyArgs().LogAsync(null!, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task RecordConsentCheck_Pending_Throws()
    {
        var user = SeedUser();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Pending, Guid.NewGuid(), null, Ct));
    }

    [HumansFact]
    public async Task RecordRejection_Stamps_ThenRefusesASecond()
    {
        var user = SeedUser(LegacyProfile(LegacyConsentCheckStatus.Cleared, isApproved: true));
        var reviewer = Guid.NewGuid();

        (await _sut.RecordRejectionAsync(user.Id, reviewer, "Spam account", Ct)).Success.Should().BeTrue();

        (await RowAsync(user.Id)).Should().BeEquivalentTo(new
        {
            Status = ConsentCheckStatus.Cleared,
            IsApproved = false,
            RejectionReason = "Spam account",
            RejectedAt = Now,
            RejectedByUserId = reviewer,
        }, o => o.ExcludingMissingMembers());
        (await _sut.RecordRejectionAsync(user.Id, reviewer, null, Ct)).ErrorKey.Should().Be("AlreadyRejected");
        (await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Cleared, reviewer, null, Ct))
            .ErrorKey.Should().Be("AlreadyRejected");
    }

    [HumansFact]
    public async Task SetConsentCheckPending_FlipsToPending()
    {
        var user = SeedUser();

        (await _sut.SetConsentCheckPendingAsync(user.Id, Ct)).Success.Should().BeTrue();

        (await RowAsync(user.Id))!.Status.Should().Be(ConsentCheckStatus.Pending);
    }

    [HumansFact]
    public async Task Writes_WithoutAProfile_ReturnNotFound()
    {
        var id = Guid.NewGuid();

        (await _sut.SetConsentCheckPendingAsync(id, Ct)).ErrorKey.Should().Be("NotFound");
        (await _sut.RecordRejectionAsync(id, Guid.NewGuid(), null, Ct)).ErrorKey.Should().Be("NotFound");
        (await RowAsync(id)).Should().BeNull();
    }

    [HumansFact]
    public async Task Copy_ListsEveryUncopiedLegacyReview_IncludingTombstones_ThenIsIdempotent()
    {
        var cleared = SeedUser(LegacyProfile(LegacyConsentCheckStatus.Cleared, isApproved: true));
        var rejected = SeedUser(LegacyProfile(rejectedAt: Earlier), state: UserState.Rejected);
        var merged = SeedUser(LegacyProfile(rejectedAt: Earlier), state: UserState.Merged, mergedAt: Earlier);
        SeedUser();

        var pending = await _sut.GetUncopiedLegacyReviewsAsync(Ct);
        pending.Select(p => p.Review.UserId).Should().BeEquivalentTo([cleared.Id, rejected.Id, merged.Id]);
        pending.Should().OnlyContain(p => p.CopyRow && !p.SetRejectedState);

        (await _sut.CopyLegacyReviewsAsync(Ct)).Should().Be(3);
        (await _sut.CopyLegacyReviewsAsync(Ct)).Should().Be(0);
        (await _sut.GetUncopiedLegacyReviewsAsync(Ct)).Should().BeEmpty();
        await _userWrite.DidNotReceiveWithAnyArgs().SetRejectedAsync(default, Arg.Any<CancellationToken>());
        (await RowAsync(cleared.Id))!.IsApproved.Should().BeTrue();
    }

    [HumansFact]
    public async Task Copy_NeverOverwritesARowWrittenSinceTheDeploy()
    {
        var user = SeedUser(LegacyProfile(LegacyConsentCheckStatus.Cleared, isApproved: true));
        await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Flagged, Guid.NewGuid(), null, Ct);

        (await _sut.GetUncopiedLegacyReviewsAsync(Ct)).Should().BeEmpty();
        (await _sut.CopyLegacyReviewsAsync(Ct)).Should().Be(0);
        (await RowAsync(user.Id))!.Status.Should().Be(ConsentCheckStatus.Flagged);
    }

    [HumansFact]
    public async Task DataMigration_IsRegistered_AndRunsTheCopy()
    {
        SeedUser(LegacyProfile(LegacyConsentCheckStatus.Pending));
        var services = new ServiceCollection().AddSingleton(_sut).BuildServiceProvider();
        var migration = new SectionDataMigrations().Migrations()
            .Should().ContainSingle(m => m.Key == "consent.profile-review").Subject;

        (await migration.Status(services, Ct)).Pending.Should().Be(1);
        (await migration.Apply(services, Guid.NewGuid(), Ct)).Should().Be(1);
        (await migration.Status(services, Ct)).Pending.Should().Be(0);
    }

    [HumansFact]
    public async Task Export_CarriesTheRows_OrNothing()
    {
        var user = SeedUser();
        var none = SeedUser();
        await _sut.RecordRejectionAsync(user.Id, Guid.NewGuid(), "dup", Ct);

        var slice = (await _sut.ContributeForUserAsync(user.Id, Ct)).Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(ConsentReviewService.ConsentReviewSlice);
        slice.Data.Should().BeEquivalentTo(new[] { new { UserId = user.Id, RejectionReason = "dup" } },
            o => o.ExcludingMissingMembers());
        (await _sut.ContributeForUserAsync(none.Id, Ct)).Single().Data.Should().BeNull();
    }

    [HumansFact]
    public async Task Erase_DropsTheFreeText_KeepsTheRecord()
    {
        var user = SeedUser();
        await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Flagged, Guid.NewGuid(), "note", Ct);
        await _sut.RecordRejectionAsync(user.Id, Guid.NewGuid(), "reason", Ct);

        await _sut.EraseForUserAsync(user.Id, Ct);

        _sut.ErasureDeclaration[ConsentReviewService.ConsentReviewSlice].Should().StartWith("Retained");
        (await RowAsync(user.Id)).Should().BeEquivalentTo(new
        {
            Status = ConsentCheckStatus.Flagged,
            Notes = (string?)null,
            RejectionReason = (string?)null,
            RejectedAt = Now,
        }, o => o.ExcludingMissingMembers());
    }

    [HumansFact]
    public void Predicates_GateOnTheReview_TheStateAndTombstones()
    {
        var active = SeedUser();
        var tombstone = SeedUser(state: UserState.Merged, mergedAt: Earlier);
        var none = ConsentReviewInfo.None(active.Id);
        var flagged = none with { Status = ConsentCheckStatus.Flagged };

        active.NeedsConsentReview(none).Should().BeTrue();
        active.NeedsConsentReview(none with { IsApproved = true }).Should().BeFalse();
        tombstone.NeedsConsentReview(none).Should().BeFalse();
        (active with { State = UserState.Rejected }).NeedsConsentReview(none).Should().BeFalse();

        active.IsConsentCheckFlagged(flagged).Should().BeTrue();
        active.IsConsentCheckFlagged(flagged with { IsApproved = true }).Should().BeTrue();
        active.IsConsentCheckFlagged(flagged with { RejectedAt = Earlier }).Should().BeFalse();
        tombstone.IsConsentCheckFlagged(flagged).Should().BeFalse();
    }

    [HumansFact]
    public void UserParts_ContributeTheReviewCardAndTheRejectionReason()
    {
        new SectionUserParts().Parts().Should().BeEquivalentTo(
        [
            new UserPart(UserPartSlots.AdminDetail, typeof(ConsentReviewCardViewComponent)),
            new UserPart(UserPartSlots.AccountStatus, typeof(RejectionReasonViewComponent)),
        ]);
    }

    [HumansFact]
    public async Task RejectionReason_RendersForSelfOnly_AndOnlyWithAReason()
    {
        var consent = Substitute.For<IConsentService>();
        var withReason = Guid.NewGuid();
        var withoutReason = Guid.NewGuid();
        consent.GetConsentReviewAsync(withReason, Arg.Any<CancellationToken>())
            .Returns(ConsentReviewInfo.None(withReason) with { RejectionReason = "dup", RejectedAt = Earlier });
        consent.GetConsentReviewAsync(withoutReason, Arg.Any<CancellationToken>())
            .Returns(ConsentReviewInfo.None(withoutReason) with { RejectedAt = Earlier });
        var component = WithContext(new RejectionReasonViewComponent(consent));

        (await component.InvokeAsync(withReason, ProfileCardViewMode.Self))
            .Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model.Should().Be("dup");
        (await component.InvokeAsync(withReason, ProfileCardViewMode.Admin))
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        (await component.InvokeAsync(withoutReason, ProfileCardViewMode.Self))
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ReviewCard_RendersForAdminOnly_WithTheRejectionAndReviewer()
    {
        var reviewer = SeedUser(UserFixtures.Profile(burnerName: "Rita", firstName: "R", lastName: "C"));
        var user = SeedUser(state: UserState.Rejected);
        var consent = Substitute.For<IConsentService>();
        consent.GetConsentReviewAsync(user.Id, Arg.Any<CancellationToken>()).Returns(ConsentReviewInfo.None(user.Id) with
        {
            Status = ConsentCheckStatus.Flagged,
            RejectionReason = "dup",
            RejectedAt = Earlier,
            RejectedByUserId = reviewer.Id,
        });
        var component = WithContext(new ConsentReviewCardViewComponent(consent, _users));

        (await component.InvokeAsync(user.Id, ProfileCardViewMode.Self))
            .Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        (await component.InvokeAsync(user.Id, ProfileCardViewMode.Admin))
            .Should().BeOfType<ViewViewComponentResult>().Which.ViewData!.Model.Should().BeEquivalentTo(new
            {
                IsRejected = true,
                Status = ConsentCheckStatus.Flagged,
                RejectionReason = "dup",
                RejectedAt = Earlier.ToDateTimeUtc(),
                RejectedByName = reviewer.BurnerName,
            });
    }

    private static T WithContext<T>(T component) where T : ViewComponent
    {
        component.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext(),
                ViewData = new ViewDataDictionary(new EmptyModelMetadataProvider(), new ModelStateDictionary()),
            },
        };
        return component;
    }
}
