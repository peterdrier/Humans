using AwesomeAssertions;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using Xunit;

namespace Humans.Domain.Tests.Entities;

public class CampSeasonTests
{
    private static readonly Instant Now = Instant.FromUtc(2026, 5, 28, 12, 0);

    [HumansFact]
    public void CreatePendingRenewal_CopiesSeasonFieldsAndSetsRequestedYear()
    {
        var source = CreateSeason(CampSeasonStatus.Active);
        source.Vibes = [CampVibe.LiveMusic, CampVibe.Wellness];

        var renewal = source.CreatePendingRenewal(Guid.NewGuid(), 2027, Now);

        renewal.CampId.Should().Be(source.CampId);
        renewal.Year.Should().Be(2027);
        renewal.Name.Should().Be(source.Name);
        renewal.Status.Should().Be(CampSeasonStatus.Pending);
        renewal.Vibes.Should().Equal(source.Vibes);
        renewal.Vibes.Should().NotBeSameAs(source.Vibes);
        renewal.CreatedAt.Should().Be(Now);
        renewal.UpdatedAt.Should().Be(Now);
    }

    [HumansFact]
    public void CreateApprovedRenewal_StartsActive()
    {
        var source = CreateSeason(CampSeasonStatus.Active);

        var renewal = source.CreateApprovedRenewal(Guid.NewGuid(), 2027, Now);

        renewal.Status.Should().Be(CampSeasonStatus.Active);
    }

    [HumansFact]
    public void Approve_FromPending_SetsReviewFields()
    {
        var season = CreateSeason(CampSeasonStatus.Pending);
        var reviewerId = Guid.NewGuid();

        season.Approve(reviewerId, "Looks good", Now);

        season.Status.Should().Be(CampSeasonStatus.Active);
        season.ReviewedByUserId.Should().Be(reviewerId);
        season.ReviewNotes.Should().Be("Looks good");
        season.ResolvedAt.Should().Be(Now);
        season.UpdatedAt.Should().Be(Now);
    }

    [HumansFact]
    public void Reject_FromPending_SetsReviewFields()
    {
        var season = CreateSeason(CampSeasonStatus.Pending);
        var reviewerId = Guid.NewGuid();

        season.Reject(reviewerId, "Needs work", Now);

        season.Status.Should().Be(CampSeasonStatus.Rejected);
        season.ReviewedByUserId.Should().Be(reviewerId);
        season.ReviewNotes.Should().Be("Needs work");
        season.ResolvedAt.Should().Be(Now);
        season.UpdatedAt.Should().Be(Now);
    }

    [HumansTheory]
    [InlineData(CampSeasonStatus.Pending)]
    [InlineData(CampSeasonStatus.Active)]
    public void Withdraw_FromOpenReviewOrActiveStatus_SetsWithdrawn(CampSeasonStatus source)
    {
        var season = CreateSeason(source);

        season.Withdraw(Now);

        season.Status.Should().Be(CampSeasonStatus.Withdrawn);
        season.UpdatedAt.Should().Be(Now);
    }

    [HumansTheory]
    [InlineData(CampSeasonStatus.Withdrawn, CampSeasonStatus.Pending)]
    [InlineData(CampSeasonStatus.Full, CampSeasonStatus.Active)]
    public void Reactivate_FromClosedStatus_RestoresExpectedStatus(
        CampSeasonStatus source,
        CampSeasonStatus expected)
    {
        var season = CreateSeason(source);

        var newStatus = season.Reactivate(Now);

        newStatus.Should().Be(expected);
        season.Status.Should().Be(expected);
        season.UpdatedAt.Should().Be(Now);
    }

    [HumansTheory]
    [InlineData(CampSeasonStatus.Active)]
    [InlineData(CampSeasonStatus.Full)]
    [InlineData(CampSeasonStatus.Rejected)]
    [InlineData(CampSeasonStatus.Withdrawn)]
    public void Approve_FromNonPendingStatus_Throws(CampSeasonStatus source)
    {
        var season = CreateSeason(source);

        var action = () => season.Approve(Guid.NewGuid(), null, Now);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"Cannot approve a season with status {source}.");
    }

    [HumansTheory]
    [InlineData(CampSeasonStatus.Active)]
    [InlineData(CampSeasonStatus.Full)]
    [InlineData(CampSeasonStatus.Rejected)]
    [InlineData(CampSeasonStatus.Withdrawn)]
    public void Reject_FromNonPendingStatus_Throws(CampSeasonStatus source)
    {
        var season = CreateSeason(source);

        var action = () => season.Reject(Guid.NewGuid(), "No", Now);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"Cannot reject a season with status {source}.");
    }

    [HumansTheory]
    [InlineData(CampSeasonStatus.Full)]
    [InlineData(CampSeasonStatus.Rejected)]
    [InlineData(CampSeasonStatus.Withdrawn)]
    public void Withdraw_FromClosedStatus_Throws(CampSeasonStatus source)
    {
        var season = CreateSeason(source);

        var action = () => season.Withdraw(Now);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"Cannot withdraw a season with status {source}.");
    }

    [HumansTheory]
    [InlineData(CampSeasonStatus.Pending)]
    [InlineData(CampSeasonStatus.Active)]
    [InlineData(CampSeasonStatus.Rejected)]
    public void Reactivate_FromInvalidStatus_Throws(CampSeasonStatus source)
    {
        var season = CreateSeason(source);

        var action = () => season.Reactivate(Now);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage($"Cannot reactivate a season with status {source}.");
    }

    private static CampSeason CreateSeason(CampSeasonStatus status) =>
        new()
        {
            Id = Guid.NewGuid(),
            CampId = Guid.NewGuid(),
            Year = 2026,
            Name = "Camp",
            Status = status,
            BlurbLong = "Long",
            BlurbShort = "Short",
            Languages = "en",
            AcceptingMembers = YesNoMaybe.Yes,
            KidsWelcome = YesNoMaybe.Maybe,
            KidsVisiting = KidsVisitingPolicy.DaytimeOnly,
            KidsAreaDescription = "Kids area",
            HasPerformanceSpace = PerformanceSpaceStatus.Yes,
            PerformanceTypes = "Music",
            Vibes = [CampVibe.ChillOut],
            AdultPlayspace = AdultPlayspacePolicy.No,
            MemberCount = 12,
            SpaceRequirement = SpaceSize.Sqm300,
            SoundZone = SoundZone.Green,
            ElectricalGrid = ElectricalGrid.Yellow,
            CreatedAt = Now,
            UpdatedAt = Now
        };
}
