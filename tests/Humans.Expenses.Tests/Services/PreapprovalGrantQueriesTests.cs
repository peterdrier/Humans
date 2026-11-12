using AwesomeAssertions;
using Humans.Expenses.Services;
using Humans.Preapprovals.Contracts;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Expenses.Tests.Services;

/// <summary>A grant held or managed under an id the person has since merged away is still theirs.</summary>
public sealed class PreapprovalGrantQueriesTests
{
    private static readonly Guid Current = Guid.NewGuid();
    private static readonly Guid Merged = Guid.NewGuid();
    private readonly IPreapprovalService _preapprovals = Substitute.For<IPreapprovalService>();

    private static GrantOption Grant(Guid holder, IReadOnlyList<Guid> managers, bool claimable,
        GrantStatus status = GrantStatus.Open) => new(
        Guid.NewGuid(), holder, managers, "Program", null, 100m, 62900500, "acc-grant",
        new LocalDate(2026, 12, 31), status, claimable);

    [HumansFact]
    public async Task ListClaimable_TheirClaimableGrants_UnderEveryIdTheyHeld()
    {
        var mine = Grant(Current, [], claimable: true);
        var underMergedId = Grant(Merged, [], claimable: true);
        var lapsed = Grant(Current, [], claimable: false);
        var someoneElses = Grant(Guid.NewGuid(), [], claimable: true);
        _preapprovals.GetGrantsAsync(Arg.Any<CancellationToken>()).Returns([mine, underMergedId, lapsed, someoneElses]);

        var claimable = await _preapprovals.ListClaimableAsync([Current, Merged], TestContext.Current.CancellationToken);

        claimable.Select(g => g.Id).Should().BeEquivalentTo([mine.Id, underMergedId.Id]);
    }

    [HumansFact]
    public async Task ListManagedGrantIds_EveryStateOfTheirPrograms_UnderEveryIdTheyHeld()
    {
        var consumed = Grant(Guid.NewGuid(), [Merged], claimable: false, GrantStatus.Consumed);
        var open = Grant(Guid.NewGuid(), [Current, Guid.NewGuid()], claimable: true);
        var elsewhere = Grant(Guid.NewGuid(), [Guid.NewGuid()], claimable: true);
        _preapprovals.GetGrantsAsync(Arg.Any<CancellationToken>()).Returns([consumed, open, elsewhere]);

        var managed = await _preapprovals.ListManagedGrantIdsAsync([Current, Merged], TestContext.Current.CancellationToken);

        managed.Should().BeEquivalentTo([consumed.Id, open.Id]);
    }
}
