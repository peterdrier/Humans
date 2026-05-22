using AwesomeAssertions;
using Humans.Application.Services.Profiles;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Tests.Services.Profiles;

// The admin list derives every status bucket + the cross-cutting "missing name" filter from UserInfo flat
// predicates (no consent lookup). These tests pin the precedence order and the filter↔badge agreement.
public class AdminHumanListAssemblerTests
{
    private static readonly IReadOnlyDictionary<Guid, string> NoEmails = new Dictionary<Guid, string>();

    private static UserInfo Build(
        bool hasProfile = true,
        bool isApproved = true,
        bool suspended = false,
        bool rejected = false,
        bool blankNames = false,
        Instant? mergedAt = null,
        Instant? deletionRequestedAt = null,
        string displayName = "Burner")
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = displayName,
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
            MergedAt = mergedAt,
            DeletionRequestedAt = deletionRequestedAt,
        };

        Profile? profile = hasProfile
            ? new Profile
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                BurnerName = blankNames ? "" : "Burner",
                FirstName = blankNames ? "" : "First",
                LastName = blankNames ? "" : "Last",
                IsApproved = isApproved,
                RejectedAt = rejected ? Instant.FromUtc(2026, 2, 1, 0, 0) : null,
                State = suspended ? ProfileState.Suspended : ProfileState.Active,
            }
            : null;

        return UserInfo.Create(user, [], [], [], profile, [], [], [], []);
    }

    private static string StatusOf(UserInfo u) =>
        AdminHumanListAssembler.Assemble([u], NoEmails, null, statusFilter: null)
            .Single().MembershipStatus;

    [HumansFact]
    public void Active_when_approved_profile()
        => StatusOf(Build()).Should().Be(MembershipStatusLabels.Active);

    [HumansFact]
    public void PendingApproval_when_profile_not_yet_approved()
        => StatusOf(Build(isApproved: false)).Should().Be(MembershipStatusLabels.PendingApproval);

    [HumansFact]
    public void Suspended_state_wins_over_approval()
        => StatusOf(Build(suspended: true)).Should().Be(MembershipStatusLabels.Suspended);

    [HumansFact]
    public void PendingDeletion_when_deletion_requested()
        => StatusOf(Build(deletionRequestedAt: Instant.FromUtc(2026, 3, 1, 0, 0)))
            .Should().Be(MembershipStatusLabels.PendingDeletion);

    [HumansFact]
    public void Merged_when_MergedAt_set()
        => StatusOf(Build(hasProfile: false, mergedAt: Instant.FromUtc(2026, 3, 1, 0, 0), displayName: "Merged User"))
            .Should().Be(MembershipStatusLabels.Merged);

    [HumansFact]
    public void Deleted_when_gdpr_anonymized_tombstone()
        => StatusOf(Build(hasProfile: false, displayName: UserInfo.GdprAnonymizedDisplayName))
            .Should().Be(MembershipStatusLabels.Deleted);

    [HumansFact]
    public void No_badge_for_genuine_no_profile_stub()
        => StatusOf(Build(hasProfile: false, displayName: "Stub")).Should().BeEmpty();

    [HumansFact]
    public void No_badge_for_rejected_profile()
        => StatusOf(Build(rejected: true)).Should().BeEmpty();

    [HumansFact]
    public void Tombstone_precedence_beats_lifecycle_state()
    {
        // A merge-source row clears its deletion fields, but assert the order is robust regardless: a row that
        // is both a tombstone and suspended must read as the terminal tombstone, never Suspended.
        var deletedButSuspended = Build(suspended: true, displayName: UserInfo.GdprAnonymizedDisplayName);
        StatusOf(deletedButSuspended).Should().Be(MembershipStatusLabels.Deleted);

        var mergedTrumpsDeletion = Build(
            hasProfile: false,
            mergedAt: Instant.FromUtc(2026, 3, 1, 0, 0),
            deletionRequestedAt: Instant.FromUtc(2026, 3, 2, 0, 0),
            displayName: "Merged User");
        StatusOf(mergedTrumpsDeletion).Should().Be(MembershipStatusLabels.Merged);
    }

    [HumansFact]
    public void HasName_filter_is_cross_cutting()
    {
        var activeNamed = Build();                       // has name → included
        var activeBlankName = Build(blankNames: true);   // no name → excluded
        var noProfile = Build(hasProfile: false, displayName: "Stub"); // no profile → excluded

        var rows = AdminHumanListAssembler.Assemble(
            [activeNamed, activeBlankName, noProfile], NoEmails, searchUserIds: null, statusFilter: "hasname");

        rows.Select(r => r.UserId)
            .Should().BeEquivalentTo([activeNamed.Id]);
    }

    [HumansFact]
    public void Status_filter_matches_the_badge_it_selects()
    {
        var active = Build();
        var pending = Build(isApproved: false);

        var rows = AdminHumanListAssembler.Assemble(
            [active, pending], NoEmails, searchUserIds: null, statusFilter: "pending");

        rows.Should().ContainSingle()
            .Which.UserId.Should().Be(pending.Id);
    }

    [HumansFact]
    public void Null_filter_returns_all_candidates()
    {
        var users = new[] { Build(), Build(isApproved: false), Build(hasProfile: false, displayName: "Stub") };

        var rows = AdminHumanListAssembler.Assemble(users, NoEmails, searchUserIds: null, statusFilter: null);

        rows.Should().HaveCount(3);
    }

    [HumansFact]
    public void SearchUserIds_prefilters_before_status()
    {
        var keep = Build();
        var drop = Build();

        var rows = AdminHumanListAssembler.Assemble(
            [keep, drop], NoEmails, searchUserIds: new HashSet<Guid> { keep.Id }, statusFilter: null);

        rows.Should().ContainSingle().Which.UserId.Should().Be(keep.Id);
    }
}
