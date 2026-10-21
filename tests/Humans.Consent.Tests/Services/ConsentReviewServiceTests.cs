using AwesomeAssertions;
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

/// <summary>
/// consent_reviews (Users decomposition commit 12): the read, the IConsentWrite annotations,
/// export and erasure, the queue predicates and the two user parts.
/// </summary>
public sealed class ConsentReviewServiceTests : IDisposable
{
    private static readonly Instant Now = Instant.FromUtc(2026, 10, 21, 12, 0);
    private static readonly Instant Earlier = Instant.FromUtc(2026, 5, 1, 9, 0);

    private readonly FakeClock _clock = new(Now);
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly List<UserInfo> _all = [];
    private readonly LegalDbContext _db;
    private readonly ConsentReviewService _sut;

    public ConsentReviewServiceTests()
    {
        var factory = new TestDbContextFactory<LegalDbContext>(
            new DbContextOptionsBuilder<LegalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db = factory.CreateDbContext();
        _sut = new ConsentReviewService(new ConsentReviewRepository(factory), _users, _clock);
        _users.GetAllRawUserInfosAsync(Arg.Any<CancellationToken>()).Returns(_ => _all.ToList());
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>A named member.</summary>
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

    private async Task SeedRowAsync(ConsentReview row)
    {
        _db.ConsentReviews.Add(row);
        await _db.SaveChangesAsync(Ct);
        _db.ChangeTracker.Clear();
    }

    private Task<ConsentReview?> RowAsync(Guid userId) =>
        _db.ConsentReviews.AsNoTracking().SingleOrDefaultAsync(r => r.UserId == userId, Ct);

    // ─── Read ────────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Read_ReturnsTheRow()
    {
        var user = SeedUser();
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

    // ─── IConsentWrite ───────────────────────────────────────────────────────────

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
    }

    [HumansFact]
    public async Task RecordConsentCheck_Flagged_ClearsApproval()
    {
        var user = SeedUser();
        await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Cleared, Guid.NewGuid(), null, Ct);

        (await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Flagged, Guid.NewGuid(), "Concern", Ct))
            .Success.Should().BeTrue();

        var row = await RowAsync(user.Id);
        row!.Status.Should().Be(ConsentCheckStatus.Flagged);
        row.IsApproved.Should().BeFalse();
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
        var user = SeedUser();
        var reviewer = Guid.NewGuid();
        await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Cleared, reviewer, null, Ct);

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

    // ─── Export, erasure ─────────────────────────────────────────────────────────

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

    // ─── Predicates ──────────────────────────────────────────────────────────────

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

    // ─── User parts ──────────────────────────────────────────────────────────────

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
