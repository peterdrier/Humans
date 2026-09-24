using Humans.Base.Attributes;
using Humans.Base.Enums;

namespace Humans.Base.Interfaces;

/// <summary>
/// Well-known user-part slot names, each named by the page that hosts it. A slot with nothing
/// in it renders nothing.
/// </summary>
public static class UserPartSlots
{
    /// <summary>The shared own/other profile page (Users <c>Profile/Index</c>).</summary>
    public const string Profile = "user-profile";

    /// <summary>The right sidebar of the shared own/other profile page, own-profile only
    /// (Users <c>Profile/Index</c>).</summary>
    public const string ProfileSidebar = "user-profile-sidebar";

    /// <summary>The admin human-detail page (Users <c>UsersAdmin/AdminDetail</c>).</summary>
    public const string AdminDetail = "user-admin-detail";

    /// <summary>The right sidebar of the admin human-detail page (Users <c>UsersAdmin/AdminDetail</c>).</summary>
    public const string AdminDetailSidebar = "user-admin-detail-sidebar";

    /// <summary>The applicant on a Board tier-application vote (Governance <c>BoardVoting/Detail</c>).</summary>
    public const string BoardVoteApplicant = "board-vote-applicant";

    /// <summary>The applicant on the onboarding review detail page (Onboarding <c>OnboardingReview/Detail</c>).</summary>
    public const string OnboardingReviewApplicant = "onboarding-review-applicant";

    /// <summary>A sender or receiver on the ticket-transfer admin detail (Tickets <c>TicketTransferAdmin/Detail</c>).</summary>
    public const string TicketTransferParty = "ticket-transfer-party";
}

/// <summary>The arguments an <see cref="IUserPart"/> component's Invoke/InvokeAsync accepts.</summary>
public sealed record UserPartArgs(Guid UserId, ProfileCardViewMode ViewMode);

/// <summary>A section-owned view component rendered into a named user-part slot.</summary>
/// <remarks>
/// The component's Invoke parameters are bound from <see cref="UserPartArgs"/> (<c>Guid userId</c>,
/// <c>ProfileCardViewMode viewMode</c>; either may be omitted). It owns authorization for any data
/// beyond the host page's own visibility and returns empty content when it has nothing to show.
/// The host supplies the target user and audience; contributors never infer them from the viewer.
/// </remarks>
public sealed record UserPart(string Slot, Type Component, int Weight = 0);

/// <summary>
/// User parts a section contributes to the slots in <see cref="UserPartSlots"/>. Returning
/// nothing is the normal case.
/// </summary>
[ViewComponentSlot(typeof(UserPartArgs))]
public interface IUserPart : ISectionContribution
{
    IEnumerable<UserPart> Parts();
}
