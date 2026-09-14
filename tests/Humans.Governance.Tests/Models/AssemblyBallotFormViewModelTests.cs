using AwesomeAssertions;
using Xunit;

using NodaTime;

using Humans.Governance.Data;
using Humans.Governance.Domain;
using Humans.Governance.Models;
using Humans.Governance.Services.Dtos;

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

    private static AssemblyVoteDetail Detail(AssemblyBallotChoice stored) =>
        new(
            Guid.NewGuid(), "Amend Art. 10", "text", "es", false, null,
            AssemblyVoteKind.RankedChoice, RequiredMajority.Simple, IndicativeAudience.None,
            BallotDisclosure.BoardOnly, AssemblyVoteStatus.Open,
            Instant.FromUtc(2026, 9, 20, 12, 0), null, null, null,
            IsOnRoster: true, IsOfficial: true, CanCastBallot: true,
            Options: [],
            OwnBallot: new AssemblyBallotView(
                stored, null, 1,
                Instant.FromUtc(2026, 9, 14, 12, 0), Instant.FromUtc(2026, 9, 14, 12, 0), []),
            Participation: new AssemblyVoteParticipation(1, 0, 1, 0, 0, 1, null));

    [HumansFact]
    public void HasDuplicateRanks_OnARankedSubmission_IsTrue()
    {
        Form(AssemblyBallotChoice.Ranked).HasDuplicateRanks().Should().BeTrue(
            "two options at rank 1 is an ambiguous ballot, and a binding vote never guesses");
    }

    [HumansFact]
    public void ChoiceToShow_PrefersTheRejectedSubmissionOverTheStoredBallot()
    {
        var vm = new AssemblyVoteDetailViewModel
        {
            Vote = Detail(stored: AssemblyBallotChoice.Abstain),
            SelectedChoice = AssemblyBallotChoice.Ranked
        };

        vm.ChoiceToShow.Should().Be(AssemblyBallotChoice.Ranked,
            "the form redisplays what the voter posted — falling back to the stored Abstain "
            + "would have them fix the rank and silently record an abstention");
    }

    [HumansFact]
    public void ChoiceToShow_OnAPlainPageLoad_IsTheStoredBallot()
    {
        var vm = new AssemblyVoteDetailViewModel { Vote = Detail(stored: AssemblyBallotChoice.Abstain) };

        vm.ChoiceToShow.Should().Be(AssemblyBallotChoice.Abstain);
    }

    [HumansFact]
    public void HasDuplicateRanks_OnAnAbstentionCarryingTheSameRanks_IsFalse()
    {
        Form(AssemblyBallotChoice.Abstain).HasDuplicateRanks().Should().BeFalse(
            "the ranks belong to a ranking the voter has just said they are not casting, and "
            + "refusing the abstention would make them tidy it up first");
    }
}
