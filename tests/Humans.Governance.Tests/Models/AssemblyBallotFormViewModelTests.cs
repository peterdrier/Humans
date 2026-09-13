using AwesomeAssertions;
using Xunit;

using Humans.Governance.Domain;
using Humans.Governance.Models;

namespace Humans.Governance.Tests.Models;

/// <summary>
/// The ballot form's own validation. The rank selectors post whichever radio the voter
/// chose, so what the form carries and what the ballot claims are two different things.
/// </summary>
public class AssemblyBallotFormViewModelTests
{
    private static AssemblyBallotFormViewModel Form(AssemblyBallotChoice? choice) =>
        new()
        {
            VoteId = Guid.NewGuid(),
            Choice = choice,
            RankedOptions =
            [
                new AssemblyRankedBallotOptionRow { OptionKey = "alpha", Selection = 1 },
                new AssemblyRankedBallotOptionRow { OptionKey = "beta", Selection = 1 }
            ]
        };

    [HumansFact]
    public void HasDuplicateRanks_OnARankedSubmission_IsTrue()
    {
        Form(AssemblyBallotChoice.Ranked).HasDuplicateRanks().Should().BeTrue(
            "two options at rank 1 is an ambiguous ballot, and a binding vote never guesses");
    }

    [HumansFact]
    public void HasDuplicateRanks_OnAnAbstentionCarryingTheSameRanks_IsFalse()
    {
        Form(AssemblyBallotChoice.Abstain).HasDuplicateRanks().Should().BeFalse(
            "the ranks belong to a ranking the voter has just said they are not casting, and "
            + "refusing the abstention would make them tidy it up first");
    }
}
