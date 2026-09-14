using AwesomeAssertions;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;
using NodaTime;
using NSubstitute;

namespace Humans.EarlyEntry.Tests.Services;

public class EarlyEntryServiceTests
{
    private static IEarlyEntryProvider ProviderReturning(params EarlyEntryGrant[] grants)
    {
        var provider = Substitute.For<IEarlyEntryProvider>();
        provider.GetEarlyEntriesAsync(Arg.Any<CancellationToken>())
                .Returns(Task.FromResult<IReadOnlyList<EarlyEntryGrant>>(grants));
        return provider;
    }

    [HumansFact]
    public async Task Roster_groups_by_user_earliest_date_wins_both_sources_listed_HasMultiple_true()
    {
        var userId = Guid.NewGuid();
        var campGrant = new EarlyEntryGrant(userId, new LocalDate(2026, 7, 7), "Camp: Flags");
        var shiftGrant = new EarlyEntryGrant(userId, new LocalDate(2026, 7, 1), "Shift: Power");

        var sut = new EarlyEntryService(new[]
        {
            ProviderReturning(campGrant),
            ProviderReturning(shiftGrant),
        });

        var roster = await sut.GetRosterAsync(Xunit.TestContext.Current.CancellationToken);

        roster.Should().ContainSingle();
        var row = roster[0];
        row.UserId.Should().Be(userId);
        row.EarliestEntryDate.Should().Be(new LocalDate(2026, 7, 1));
        row.Sources.Should().Equal("Camp: Flags", "Shift: Power");
        row.HasMultiple.Should().BeTrue();
    }

    [HumansFact]
    public async Task Roster_keeps_one_row_per_user()
    {
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var sut = new EarlyEntryService(new[]
        {
            ProviderReturning(
                new EarlyEntryGrant(alice, new LocalDate(2026, 7, 7), "Camp: Flags"),
                new EarlyEntryGrant(bob, new LocalDate(2026, 7, 3), "Camp: Flags")),
            ProviderReturning(new EarlyEntryGrant(alice, new LocalDate(2026, 7, 1), "Shift: Power")),
        });

        var roster = await sut.GetRosterAsync(Xunit.TestContext.Current.CancellationToken);

        roster.Should().HaveCount(2);
        var aliceRow = roster.Single(r => r.UserId == alice);
        aliceRow.EarliestEntryDate.Should().Be(new LocalDate(2026, 7, 1));
        aliceRow.Sources.Should().Equal("Camp: Flags", "Shift: Power");
        var bobRow = roster.Single(r => r.UserId == bob);
        bobRow.EarliestEntryDate.Should().Be(new LocalDate(2026, 7, 3));
        bobRow.Sources.Should().Equal("Camp: Flags");
        bobRow.HasMultiple.Should().BeFalse();
    }

    [HumansFact]
    public async Task Same_source_label_twice_is_one_source_and_not_multiple()
    {
        var userId = Guid.NewGuid();
        var sut = new EarlyEntryService(new[]
        {
            ProviderReturning(new EarlyEntryGrant(userId, new LocalDate(2026, 7, 7), "Camp: Flags")),
            ProviderReturning(new EarlyEntryGrant(userId, new LocalDate(2026, 7, 5), "Camp: Flags")),
        });

        var roster = await sut.GetRosterAsync(Xunit.TestContext.Current.CancellationToken);
        var mine = await sut.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);

        roster.Should().ContainSingle();
        roster[0].EarliestEntryDate.Should().Be(new LocalDate(2026, 7, 5));
        roster[0].Sources.Should().Equal("Camp: Flags");
        roster[0].HasMultiple.Should().BeFalse();
        mine.Should().NotBeNull();
        mine.Sources.Should().Equal("Camp: Flags");
    }

    [HumansFact]
    public async Task Single_source_is_not_flagged_HasMultiple_false()
    {
        var userId = Guid.NewGuid();
        var grant = new EarlyEntryGrant(userId, new LocalDate(2026, 7, 7), "Camp: Flags");

        var sut = new EarlyEntryService(new[] { ProviderReturning(grant) });

        var roster = await sut.GetRosterAsync(Xunit.TestContext.Current.CancellationToken);

        roster.Should().ContainSingle();
        roster[0].HasMultiple.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetForUserAsync_returns_earliest_and_sources_or_null_for_unknown()
    {
        var userId = Guid.NewGuid();
        var campGrant = new EarlyEntryGrant(userId, new LocalDate(2026, 7, 7), "Camp: Flags");
        var shiftGrant = new EarlyEntryGrant(userId, new LocalDate(2026, 7, 1), "Shift: Power");

        var sut = new EarlyEntryService(new[]
        {
            ProviderReturning(campGrant),
            ProviderReturning(shiftGrant),
        });

        var found = await sut.GetForUserAsync(userId, Xunit.TestContext.Current.CancellationToken);
        found.Should().NotBeNull();
        found.EarliestEntryDate.Should().Be(new LocalDate(2026, 7, 1));
        found.Sources.Should().Equal("Camp: Flags", "Shift: Power");

        var notFound = await sut.GetForUserAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        notFound.Should().BeNull();
    }
}
