using AwesomeAssertions;
using Xunit;
using Humans.Shifts.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Tests.Infrastructure;

namespace Humans.Shifts.Tests.Services;

/// <summary>
/// Period admission for the team-wide coordinator message's audience filter.
/// The two traps: the hidden period boxes must not narrow an upcoming-scoped send,
/// and a whole-event rota belongs to whichever periods are selected.
/// </summary>
public sealed class TeamRotasAudienceFilterTests
{
    [HumansTheory]
    [InlineData(RotaPeriod.Build)]
    [InlineData(RotaPeriod.Event)]
    [InlineData(RotaPeriod.Strike)]
    [InlineData(RotaPeriod.All)]
    public void UpcomingOnly_AdmitsEveryPeriod_WhateverThePeriodFlagsHold(RotaPeriod period) =>
        new TeamRotasAudienceFilter(UpcomingOnly: true, false, false, false)
            .Includes(period).Should().BeTrue();

    [HumansFact]
    public void PeriodFlags_SelectTheirOwnPeriod()
    {
        var buildOnly = new TeamRotasAudienceFilter(UpcomingOnly: false, IncludeBuild: true, IncludeEvent: false, IncludeStrike: false);

        buildOnly.Includes(RotaPeriod.Build).Should().BeTrue();
        buildOnly.Includes(RotaPeriod.Event).Should().BeFalse();
        buildOnly.Includes(RotaPeriod.Strike).Should().BeFalse();
    }

    [HumansFact]
    public void WholeEventRota_RidesOnAnySelectedPeriod()
    {
        new TeamRotasAudienceFilter(UpcomingOnly: false, false, false, IncludeStrike: true)
            .Includes(RotaPeriod.All).Should().BeTrue();

        new TeamRotasAudienceFilter(UpcomingOnly: false, false, false, false)
            .Includes(RotaPeriod.All).Should().BeFalse();
    }

    [HumansFact]
    public void Default_IsTheComposeFormsInitialState() =>
        TeamRotasAudienceFilter.Default.Should()
            .Be(new TeamRotasAudienceFilter(true, true, true, true));
}
