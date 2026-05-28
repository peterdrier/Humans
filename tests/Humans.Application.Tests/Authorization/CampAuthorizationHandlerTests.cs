using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application.Interfaces.Camps;
using Humans.Domain.Constants;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Authorization.Requirements;
using Microsoft.AspNetCore.Authorization;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Authorization;

public sealed class CampAuthorizationHandlerTests
{
    private readonly ICampServiceRead _campService = Substitute.For<ICampServiceRead>();
    private readonly CampAuthorizationHandler _handler;

    private static readonly Guid LeadCampId = Guid.NewGuid();
    private static readonly Guid OtherCampId = Guid.NewGuid();
    private static readonly Guid LeadCampSeasonId = Guid.NewGuid();
    private static readonly Guid OtherCampSeasonId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid WorkshopUserId = Guid.NewGuid();

    public CampAuthorizationHandlerTests()
    {
        _handler = new CampAuthorizationHandler(_campService);
        _campService.GetSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(new CampSettingsInfo(2026, [], null));
        _campService.GetCampsForYearAsync(2026, Arg.Any<CancellationToken>())
            .Returns(new List<CampInfo>
            {
                CreateCampInfo("lead"),
                CreateCampInfo("other")
            });

    }

    public static TheoryData<string, string, bool> CampAuthorizationCases => new()
    {
        { "admin", "other", true },
        { "camp-admin", "other", true },
        { "lead", "lead", true },
        { "lead", "other", false },
        { "regular", "lead", false },
        { "anonymous", "lead", false },
        { "invalid-id", "lead", false },
    };

    [HumansTheory]
    [MemberData(nameof(CampAuthorizationCases))]
    public async Task Camp_authorization_matches_expected_scenarios(
        string userKind,
        string campKind,
        bool expected)
    {
        var regularUserId = Guid.NewGuid();
        var user = CreateUser(userKind, regularUserId);
        var camp = CreateCamp(campKind);
        var campInfo = CreateCampInfo(campKind);
        var campId = camp.Id;

        var result = await EvaluateAsync(user, camp, CampOperationRequirement.Manage);
        var infoResult = await EvaluateAsync(user, campInfo, CampOperationRequirement.Manage);
        var idResult = await EvaluateAsync(user, campId, CampOperationRequirement.Manage);

        result.Should().Be(expected);
        infoResult.Should().Be(expected);
        idResult.Should().Be(expected);
    }

    public static TheoryData<string, string, bool> SubmitEventCases => new()
    {
        { "admin", "other", true },
        { "camp-admin", "other", true },
        { "lead", "lead", true },          // Lead role holder
        { "lead", "other", false },
        { "workshop", "lead", true },      // Workshop role holder for the camp
        { "workshop", "other", false },    // Workshop role holder, but not for THIS camp
        { "regular", "lead", false },      // role holder for a non-Lead/Workshop role
        { "anonymous", "lead", false },
        { "invalid-id", "lead", false },
    };

    [HumansTheory]
    [MemberData(nameof(SubmitEventCases))]
    public async Task SubmitEvent_authorization_matches_expected_scenarios(
        string userKind,
        string campKind,
        bool expected)
    {
        var regularUserId = Guid.NewGuid();
        var user = CreateUser(userKind, regularUserId);
        var camp = CreateCamp(campKind);
        var campInfo = CreateCampInfo(campKind);
        var campId = camp.Id;

        var result = await EvaluateAsync(user, camp, CampOperationRequirement.SubmitEvent);
        var infoResult = await EvaluateAsync(user, campInfo, CampOperationRequirement.SubmitEvent);
        var idResult = await EvaluateAsync(user, campId, CampOperationRequirement.SubmitEvent);

        result.Should().Be(expected);
        infoResult.Should().Be(expected);
        idResult.Should().Be(expected);
    }

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, object resource, CampOperationRequirement requirement)
    {
        var context = new AuthorizationHandlerContext([requirement], user, resource);

        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static Camp CreateCamp(string kind) =>
        new()
        {
            Id = kind switch
            {
                "lead" => LeadCampId,
                "other" => OtherCampId,
                _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
            }
        };

    private static CampInfo CreateCampInfo(string kind)
    {
        var isLeadCamp = string.Equals(kind, "lead", StringComparison.Ordinal);
        var campId = kind switch
        {
            "lead" => LeadCampId,
            "other" => OtherCampId,
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        return new CampInfo(
            campId,
            Slug: "camp",
            ContactEmail: "camp@example.com",
            ContactPhone: string.Empty,
            IsSwissCamp: false,
            TimesAtNowhere: 0,
            Seasons:
            [
                new CampSeasonInfo(
                    isLeadCamp ? LeadCampSeasonId : OtherCampSeasonId,
                    campId,
                    "camp",
                    2026,
                    null,
                    "Camp",
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
                    LeadUserIds = isLeadCamp ? [UserId] : [],
                    WorkshopLeadUserIds = isLeadCamp ? [WorkshopUserId] : []
                }
            ]);
    }

    private static ClaimsPrincipal CreateUser(string kind, Guid regularUserId) =>
        kind switch
        {
            "admin" => CreateUserWithRoles(RoleNames.Admin),
            "camp-admin" => CreateUserWithRoles(RoleNames.CampAdmin),
            "lead" => CreateUserWithId(UserId),
            "workshop" => CreateUserWithId(WorkshopUserId),
            "regular" => CreateUserWithId(regularUserId),
            "anonymous" => new ClaimsPrincipal(new ClaimsIdentity()),
            "invalid-id" => new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, "not-a-guid"),
                new Claim(ClaimTypes.Name, "test@example.com")
            ], "TestAuth")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

    private static ClaimsPrincipal CreateUserWithRoles(params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new(ClaimTypes.Name, "admin@example.com")
        };
        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }

    private static ClaimsPrincipal CreateUserWithId(Guid userId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, "lead@example.com")
        };
        return new ClaimsPrincipal(new ClaimsIdentity(claims, "TestAuth"));
    }
}
