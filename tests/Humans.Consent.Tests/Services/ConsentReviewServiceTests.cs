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
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly LegalDbContext _db;
    private readonly ConsentReviewService _sut;

    public ConsentReviewServiceTests()
    {
        var factory = new TestDbContextFactory<LegalDbContext>(
            new DbContextOptionsBuilder<LegalDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        _db = factory.CreateDbContext();
        _sut = new ConsentReviewService(new ConsentReviewRepository(factory), _users, _audit, _clock);
    }

    public void Dispose() => _db.Dispose();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private UserInfo SeedUser(
        ProfileInfo? profile = null, UserState state = UserState.Active, Guid? id = null, Instant? mergedAt = null)
    {
        var userId = id ?? Guid.NewGuid();
        var info = UserInfo.Create(
            new User { Id = userId, BurnerName = "Ana", CreatedAt = Now, State = state, MergedAt = mergedAt },
            [], [],
            profile ?? UserFixtures.Profile(burnerName: "Ana", firstName: "Ana", lastName: "García"));
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
        _users.GetRawUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
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

    [HumansFact]
    public async Task Read_UsesTheStoredReview()
    {
        var user = SeedUser();
        await SeedRowAsync(new ConsentReview
        {
            UserId = user.Id,
            Status = ConsentCheckStatus.Cleared,
            IsApproved = true,
            Notes = "ok",
            UpdatedAt = Now,
        });

        var review = await _sut.GetReviewAsync(user.Id, Ct);
        review.Status.Should().Be(ConsentCheckStatus.Cleared);
        review.IsApproved.Should().BeTrue();
        review.Notes.Should().Be("ok");
        (await _sut.GetReviewsAsync(Ct))[user.Id].Should().Be(review);

        await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Flagged, Guid.NewGuid(), null, Ct);

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
    public async Task RejectedAccountAndReview_ReadRejectedAndRefuseRepeatedRejection()
    {
        var user = SeedUser(state: UserState.Rejected);
        await SeedRowAsync(new ConsentReview
        {
            UserId = user.Id,
            Status = ConsentCheckStatus.Flagged,
            RejectedAt = Earlier,
            RejectionReason = "dup",
            UpdatedAt = Now,
        });

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

    [HumansTheory]
    [InlineData("clear", 4000)]
    [InlineData("clear", 4001)]
    [InlineData("flag", 4000)]
    [InlineData("flag", 4001)]
    [InlineData("reject", 4000)]
    [InlineData("reject", 4001)]
    public async Task ReviewFreeTextRespectsStorageLimit_WithoutTruncatingUnicode(string action, int characters)
    {
        var user = SeedUser();
        var text = string.Concat(Enumerable.Repeat("😀", characters));
        var reviewer = Guid.NewGuid();

        var result = action switch
        {
            "clear" => await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Cleared, reviewer, text, Ct),
            "flag" => await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Flagged, reviewer, text, Ct),
            "reject" => await _sut.RecordRejectionAsync(user.Id, reviewer, text, Ct),
            _ => throw new ArgumentOutOfRangeException(nameof(action)),
        };

        result.Success.Should().Be(characters <= 4000);
        if (characters > 4000)
        {
            result.ErrorKey.Should().Be("ReviewTextTooLong");
            (await _db.ConsentReviews.AnyAsync(r => r.UserId == user.Id, Ct)).Should().BeFalse();
            _audit.ReceivedCalls().Should().BeEmpty();
            return;
        }

        var row = await RowAsync(user.Id);
        row.Should().NotBeNull();
        (string.Equals(action, "reject", StringComparison.Ordinal) ? row!.RejectionReason : row!.Notes)
            .Should().Be(text);
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OversizedText_DoesNotHideExistingRejection(bool clear)
    {
        var user = SeedUser();
        var reviewer = Guid.NewGuid();
        await _sut.RecordRejectionAsync(user.Id, reviewer, "Original reason", Ct);
        var text = new string('a', 4001);

        var result = clear
            ? await _sut.RecordConsentCheckAsync(user.Id, ConsentCheckStatus.Cleared, reviewer, text, Ct)
            : await _sut.RecordRejectionAsync(user.Id, reviewer, text, Ct);

        result.ErrorKey.Should().Be("AlreadyRejected");
        (await RowAsync(user.Id))!.RejectionReason.Should().Be("Original reason");
        _audit.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task RecordConsentCheck_Flagged_ClearsApproval()
    {
        var user = SeedUser();
        await SeedRowAsync(new ConsentReview
        {
            UserId = user.Id,
            Status = ConsentCheckStatus.Cleared,
            IsApproved = true,
            UpdatedAt = Now,
        });

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
        var user = SeedUser();
        await SeedRowAsync(new ConsentReview
        {
            UserId = user.Id,
            Status = ConsentCheckStatus.Cleared,
            IsApproved = true,
            UpdatedAt = Now,
        });
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
        // Fake accounts (gate terminal, dev personas) never count, so the queue, pill and meters agree.
        (active with { IsFake = true }).NeedsConsentReview(none).Should().BeFalse();

        active.IsConsentCheckFlagged(flagged).Should().BeTrue();
        active.IsConsentCheckFlagged(flagged with { IsApproved = true }).Should().BeTrue();
        active.IsConsentCheckFlagged(flagged with { RejectedAt = Earlier }).Should().BeFalse();
        tombstone.IsConsentCheckFlagged(flagged).Should().BeFalse();
        (active with { IsFake = true }).IsConsentCheckFlagged(flagged).Should().BeFalse();
    }

    [HumansFact]
    public void UserParts_ContributeTheReviewCardAndTheRejectionReason()
    {
        ((IUserPart)new Section()).Parts().Should().BeEquivalentTo(
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
