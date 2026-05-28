using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.CityPlanning;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Authorization;

public sealed class ContainerAuthorizationHandlerTests
{
    private static readonly Guid CampId = Guid.NewGuid();
    private static readonly Guid LeadUserId = Guid.NewGuid();

    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly ICityPlanningService _cityPlanningService = Substitute.For<ICityPlanningService>();
    private readonly ContainerAuthorizationHandler _handler;

    public ContainerAuthorizationHandlerTests()
    {
        _handler = new ContainerAuthorizationHandler(_campService, _cityPlanningService);
        _cityPlanningService.IsCityPlanningTeamMemberAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(false);
    }

    [HumansFact]
    public async Task Place_CampLead_UsesCityPlanningYearInsteadOfCampPublicYear()
    {
        _cityPlanningService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(MakeSettings(year: 2027, isContainerPlacementOpen: true));
        _campService.GetCampsForYearAsync(2027, Arg.Any<CancellationToken>())
            .Returns([CreateCampInfo(year: 2027, isLead: true)]);

        var result = await EvaluateAsync(
            CreateUserWithId(LeadUserId),
            ContainerOperationRequirement.Place);

        result.Should().BeTrue();
        await _campService.Received(1).GetCampsForYearAsync(2027, Arg.Any<CancellationToken>());
        await _campService.DidNotReceive().GetSettingsAsync(Arg.Any<CancellationToken>());
        await _campService.DidNotReceive().GetCampsForYearAsync(2026, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Place_CampLead_DeniesWhenContainerPlacementIsClosed()
    {
        _cityPlanningService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(MakeSettings(year: 2027, isContainerPlacementOpen: false));

        var result = await EvaluateAsync(
            CreateUserWithId(LeadUserId),
            ContainerOperationRequirement.Place);

        result.Should().BeFalse();
        await _campService.DidNotReceive().GetCampsForYearAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, ContainerOperationRequirement requirement)
    {
        var context = new AuthorizationHandlerContext(
            [requirement],
            user,
            ContainerAuthorizationTarget.ForCamp(CampId));

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static CityPlanningSettingsDto MakeSettings(int year, bool isContainerPlacementOpen) =>
        new(
            Id: Guid.NewGuid(),
            Year: year,
            IsPlacementOpen: false,
            OpenedAt: null,
            ClosedAt: null,
            PlacementOpensAt: null,
            PlacementClosesAt: null,
            RegistrationInfo: null,
            LimitZoneGeoJson: null,
            OfficialZonesGeoJson: null,
            IsContainerPlacementOpen: isContainerPlacementOpen,
            ContainerPlacementOpenedAt: null,
            ContainerPlacementClosedAt: null,
            UpdatedAt: Instant.FromUtc(2026, 5, 28, 0, 0));

    private static CampInfo CreateCampInfo(int year, bool isLead) =>
        new(
            CampId,
            Slug: "lead-camp",
            ContactEmail: "camp@example.com",
            ContactPhone: string.Empty,
            IsSwissCamp: false,
            TimesAtNowhere: 0,
            Seasons:
            [
                new CampSeasonInfo(
                    Guid.NewGuid(),
                    CampId,
                    "lead-camp",
                    year,
                    null,
                    "Lead Camp",
                    string.Empty,
                    string.Empty,
                    [],
                    CampSeasonStatus.Active,
                    YesNoMaybe.Yes,
                    YesNoMaybe.No,
                    AdultPlayspacePolicy.No,
                    MemberCount: 0,
                    SoundZone: null,
                    SpaceRequirement: null,
                    ElectricalGrid: null,
                    EeSlotCount: 0,
                    EeGrantedCount: null,
                    JoinedMemberCount: null)
                {
                    LeadUserIds = isLead ? [LeadUserId] : []
                }
            ]);

    private static ClaimsPrincipal CreateUserWithId(Guid userId) =>
        new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
            new Claim(ClaimTypes.Name, "lead@example.com")
        ], "TestAuth"));
}
