using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Users.Tests;

public sealed class SectionUserPartsTests
{
    [HumansFact]
    public void Parts_ContributesProfileCardAndHumanSummaryToForeignHostSlots()
    {
        IUserPart contribution = new SectionUserParts();

        var parts = contribution.Parts();

        parts.Select(p => p.Slot).Should().BeEquivalentTo(
        [
            UserPartSlots.BoardVoteApplicant,
            UserPartSlots.OnboardingReviewApplicant,
            UserPartSlots.TicketTransferParty,
        ]);
    }
}
