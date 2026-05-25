using AwesomeAssertions;
using Humans.Application.Interfaces.EarlyEntry;
using Humans.Application.Services.EarlyEntry;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services.EarlyEntry;

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

        var roster = await sut.GetRosterAsync(CancellationToken.None);

        roster.Should().ContainSingle();
        var row = roster[0];
        row.UserId.Should().Be(userId);
        row.EarliestEntryDate.Should().Be(new LocalDate(2026, 7, 1));
        row.Sources.Should().HaveCount(2);
        row.Sources.Should().Contain("Camp: Flags");
        row.Sources.Should().Contain("Shift: Power");
        row.HasMultiple.Should().BeTrue();
    }

    [HumansFact]
    public async Task Single_source_is_not_flagged_HasMultiple_false()
    {
        var userId = Guid.NewGuid();
        var grant = new EarlyEntryGrant(userId, new LocalDate(2026, 7, 7), "Camp: Flags");

        var sut = new EarlyEntryService(new[] { ProviderReturning(grant) });

        var roster = await sut.GetRosterAsync(CancellationToken.None);

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

        var found = await sut.GetForUserAsync(userId, CancellationToken.None);
        found.Should().NotBeNull();
        found!.EarliestEntryDate.Should().Be(new LocalDate(2026, 7, 1));
        found.Sources.Should().HaveCount(2);

        var notFound = await sut.GetForUserAsync(Guid.NewGuid(), CancellationToken.None);
        notFound.Should().BeNull();
    }
}
