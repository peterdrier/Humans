using AwesomeAssertions;
using Humans.Base.Interfaces;

namespace Humans.Users.Tests;

public sealed class SectionUserPartsTests
{
    [HumansFact]
    public void Parts_ContributesProfileCardToForeignHostSlots()
    {
        IUserPart contribution = new Section();

        var parts = contribution.Parts();

        parts.Select(p => p.Slot).Should().BeEquivalentTo(
        [
            UserPartSlots.BoardVoteApplicant,
            UserPartSlots.ConsentReviewApplicant,
        ]);
    }
}
