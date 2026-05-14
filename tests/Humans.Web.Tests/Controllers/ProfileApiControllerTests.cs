using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Testing;
using Humans.Web.Controllers;
using Humans.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Web.Tests.Controllers;

/// <summary>
/// Privacy-gate coverage for <see cref="ProfileApiController"/>.
///
/// <para>
/// The two endpoints under test (<c>GET /api/profiles/search</c> and
/// <c>GET /api/profiles/by-userid/{userId}</c>) both feed each result row
/// through <c>GetSharedDetailAsync</c>, whose job is to pick a viewer-visible
/// disambiguation line: viewer-visible primary email → highest-priority
/// visible contact field (Phone → Signal → Telegram → WhatsApp → Discord →
/// Other) → null. Legal name is never surfaced (dropped from the priority
/// chain at PR #538 review).
/// </para>
///
/// <para>
/// These tests target the load-bearing privacy concern: a row only ever
/// surfaces data the current viewer is allowed to see, as decided by
/// <c>IContactFieldService.GetViewerAccessLevelAsync</c> +
/// <c>IUserEmailService.GetVisibleEmailsAsync</c> +
/// <c>IContactFieldService.GetVisibleContactFieldsAsync</c>. The controller
/// is verified to delegate visibility decisions to those services, not to
/// re-implement them.
/// </para>
/// </summary>
public class ProfileApiControllerTests
{
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IContactFieldService _contactFieldService = Substitute.For<IContactFieldService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly UserManager<User> _userManager;

    public ProfileApiControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);

        // Picture URLs are not the focus here — return an empty dictionary so the
        // helper returns null URLs for every result row.
        _profileService
            .GetCustomPictureInfoByUserIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>());
    }

    private ProfileApiController BuildSut(User? currentUser)
    {
        if (currentUser is not null)
        {
            _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(currentUser);
        }

        var ctrl = new ProfileApiController(
            _profileService, _contactFieldService, _userEmailService, _userManager);

        var http = new DefaultHttpContext();
        if (currentUser is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, currentUser.Id.ToString()) },
                "test"));
        }

        var services = new ServiceCollection();
        services.AddSingleton(NullLoggerFactory.Instance);
        var urlHelperFactory = Substitute.For<IUrlHelperFactory>();
        urlHelperFactory.GetUrlHelper(Arg.Any<ActionContext>())
            .Returns(Substitute.For<IUrlHelper>());
        services.AddSingleton(urlHelperFactory);
        http.RequestServices = services.BuildServiceProvider();

        ctrl.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor
            {
                ActionName = "Test",
            },
        };
        ctrl.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        ctrl.Url = Substitute.For<IUrlHelper>();
        return ctrl;
    }

    private static HumanSearchResult MakeSearchResult(Guid userId, Guid profileId, string burnerName) =>
        new(
            UserId: userId,
            ProfileId: profileId,
            BurnerName: burnerName,
            ProfilePictureUrl: null,
            MatchField: "Name",
            MatchSnippet: null,
            MatchedEmail: null);

    private static User MakeUser(Guid id) =>
        new() { Id = id, Email = $"viewer-{id:N}@example.com", DisplayName = "Viewer" };

    private static FullProfile MakeFullProfile(
        Guid userId,
        Guid profileId,
        string? burnerName = "Target Burner",
        bool isRejected = false) =>
        new(
            UserId: userId,
            DisplayName: "Target Display",
            ProfilePictureUrl: null,
            HasCustomPicture: false,
            ProfileId: profileId,
            UpdatedAtTicks: 0,
            BurnerName: burnerName,
            Bio: null,
            Pronouns: null,
            ContributionInterests: null,
            City: null,
            CountryCode: null,
            Latitude: null,
            Longitude: null,
            BirthdayDay: null,
            BirthdayMonth: null,
            IsApproved: true,
            IsSuspended: false,
            CVEntries: Array.Empty<CVEntry>(),
            IsRejected: isRejected);

    // ==========================================================================
    // Search — privacy gate behavior on the per-row detail line.
    // ==========================================================================

    [HumansFact]
    public async Task Search_returns_null_detail_when_viewer_is_anonymous()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        _profileService.SearchProfilesAsync(Arg.Any<string>(),
                Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeSearchResult(userId, profileId, "David") });

        var sut = BuildSut(currentUser: null);

        var result = await sut.Search(q: "David");

        var rows = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IEnumerable<HumanLookupSearchResult>>().Subject.ToList();
        rows.Should().ContainSingle().Which.Detail.Should().BeNull();

        // No DB / service calls were made for visibility resolution.
        await _contactFieldService.DidNotReceive().GetViewerAccessLevelAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _userEmailService.DidNotReceive().GetVisibleEmailsAsync(
            Arg.Any<Guid>(), Arg.Any<ContactFieldVisibility>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Search_surfaces_visible_primary_email_as_detail()
    {
        var viewer = MakeUser(Guid.NewGuid());
        var targetUserId = Guid.NewGuid();
        var targetProfileId = Guid.NewGuid();

        _profileService.SearchProfilesAsync(Arg.Any<string>(),
                Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeSearchResult(targetUserId, targetProfileId, "David") });

        _contactFieldService.GetViewerAccessLevelAsync(targetUserId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(ContactFieldVisibility.AllActiveProfiles);

        _userEmailService.GetVisibleEmailsAsync(targetUserId,
                ContactFieldVisibility.AllActiveProfiles, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UserEmailDto(Guid.NewGuid(), "alt@example.com",
                    IsVerified: true, IsGoogle: false, Provider: null, ProviderKey: null,
                    IsPrimary: false, Visibility: ContactFieldVisibility.AllActiveProfiles),
                new UserEmailDto(Guid.NewGuid(), "primary@example.com",
                    IsVerified: true, IsGoogle: false, Provider: null, ProviderKey: null,
                    IsPrimary: true, Visibility: ContactFieldVisibility.AllActiveProfiles),
            });

        var sut = BuildSut(viewer);

        var result = await sut.Search(q: "David");

        var row = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IEnumerable<HumanLookupSearchResult>>().Subject.Single();
        row.Detail.Should().Be("primary@example.com");

        // GetByUserIdsAsync (DB-bound) is bypassed — ProfileId comes from HumanSearchResult.
        await _profileService.DidNotReceive().GetByUserIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Search_falls_back_to_contact_field_in_priority_order_when_no_visible_email()
    {
        var viewer = MakeUser(Guid.NewGuid());
        var targetUserId = Guid.NewGuid();
        var targetProfileId = Guid.NewGuid();

        _profileService.SearchProfilesAsync(Arg.Any<string>(),
                Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeSearchResult(targetUserId, targetProfileId, "David") });

        _contactFieldService.GetViewerAccessLevelAsync(targetUserId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(ContactFieldVisibility.AllActiveProfiles);
        _userEmailService.GetVisibleEmailsAsync(targetUserId, Arg.Any<ContactFieldVisibility>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserEmailDto>());

        // Mix of types — Phone must win over Signal regardless of insert order.
        _contactFieldService.GetVisibleContactFieldsAsync(targetProfileId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ContactFieldDto(Guid.NewGuid(), ContactFieldType.Signal, "Signal", "signal-handle",
                    ContactFieldVisibility.AllActiveProfiles),
                new ContactFieldDto(Guid.NewGuid(), ContactFieldType.Phone, "Phone", "+1-555-0100",
                    ContactFieldVisibility.AllActiveProfiles),
            });

        var sut = BuildSut(viewer);

        var result = await sut.Search(q: "David");

        var row = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IEnumerable<HumanLookupSearchResult>>().Subject.Single();
        row.Detail.Should().Be("Phone +1-555-0100");
    }

    [HumansFact]
    public async Task Search_returns_null_detail_when_viewer_can_see_nothing()
    {
        var viewer = MakeUser(Guid.NewGuid());
        var targetUserId = Guid.NewGuid();
        var targetProfileId = Guid.NewGuid();

        _profileService.SearchProfilesAsync(Arg.Any<string>(),
                Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeSearchResult(targetUserId, targetProfileId, "David") });

        _contactFieldService.GetViewerAccessLevelAsync(targetUserId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(ContactFieldVisibility.AllActiveProfiles);
        _userEmailService.GetVisibleEmailsAsync(targetUserId, Arg.Any<ContactFieldVisibility>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserEmailDto>());
        _contactFieldService.GetVisibleContactFieldsAsync(targetProfileId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ContactFieldDto>());

        var sut = BuildSut(viewer);

        var result = await sut.Search(q: "David");

        result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IEnumerable<HumanLookupSearchResult>>().Subject
            .Single().Detail.Should().BeNull();
    }

    [HumansFact]
    public async Task Search_skips_obsolete_email_contact_field_type()
    {
        var viewer = MakeUser(Guid.NewGuid());
        var targetUserId = Guid.NewGuid();
        var targetProfileId = Guid.NewGuid();

        _profileService.SearchProfilesAsync(Arg.Any<string>(),
                Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new[] { MakeSearchResult(targetUserId, targetProfileId, "David") });

        _contactFieldService.GetViewerAccessLevelAsync(targetUserId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(ContactFieldVisibility.AllActiveProfiles);
        _userEmailService.GetVisibleEmailsAsync(targetUserId, Arg.Any<ContactFieldVisibility>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserEmailDto>());

#pragma warning disable CS0618 // Verifying the controller skips the obsolete Email enum value.
        _contactFieldService.GetVisibleContactFieldsAsync(targetProfileId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new ContactFieldDto(Guid.NewGuid(), ContactFieldType.Email, "Email", "obsolete@example.com",
                    ContactFieldVisibility.AllActiveProfiles),
                new ContactFieldDto(Guid.NewGuid(), ContactFieldType.Discord, "Discord", "user#1234",
                    ContactFieldVisibility.AllActiveProfiles),
            });
#pragma warning restore CS0618

        var sut = BuildSut(viewer);

        var result = await sut.Search(q: "David");

        var row = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IEnumerable<HumanLookupSearchResult>>().Subject.Single();
        row.Detail.Should().Be("Discord user#1234");
    }

    // ==========================================================================
    // GetByUserId — single-person lookup. Same privacy gate, plus 404 paths.
    // ==========================================================================

    [HumansFact]
    public async Task GetByUserId_returns_404_when_full_profile_not_in_cache()
    {
        var viewer = MakeUser(Guid.NewGuid());
        _profileService.GetFullProfileAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((FullProfile?)null);

        var sut = BuildSut(viewer);

        var result = await sut.GetByUserId(Guid.NewGuid());

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task GetByUserId_returns_404_when_profile_is_rejected()
    {
        var viewer = MakeUser(Guid.NewGuid());
        var targetUserId = Guid.NewGuid();
        var targetProfileId = Guid.NewGuid();

        _profileService.GetFullProfileAsync(targetUserId, Arg.Any<CancellationToken>())
            .Returns(MakeFullProfile(targetUserId, targetProfileId, isRejected: true));

        var sut = BuildSut(viewer);

        var result = await sut.GetByUserId(targetUserId);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task GetByUserId_returns_picker_row_with_viewer_visible_email()
    {
        var viewer = MakeUser(Guid.NewGuid());
        var targetUserId = Guid.NewGuid();
        var targetProfileId = Guid.NewGuid();

        _profileService.GetFullProfileAsync(targetUserId, Arg.Any<CancellationToken>())
            .Returns(MakeFullProfile(targetUserId, targetProfileId, burnerName: "Davey"));
        _contactFieldService.GetViewerAccessLevelAsync(targetUserId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(ContactFieldVisibility.AllActiveProfiles);
        _userEmailService.GetVisibleEmailsAsync(targetUserId, Arg.Any<ContactFieldVisibility>(), Arg.Any<CancellationToken>())
            .Returns(new[]
            {
                new UserEmailDto(Guid.NewGuid(), "shared@example.com",
                    IsVerified: true, IsGoogle: false, Provider: null, ProviderKey: null,
                    IsPrimary: true, Visibility: ContactFieldVisibility.AllActiveProfiles),
            });

        var sut = BuildSut(viewer);

        var result = await sut.GetByUserId(targetUserId);

        var row = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<HumanLookupSearchResult>().Subject;
        row.UserId.Should().Be(targetUserId);
        row.DisplayName.Should().Be("Davey");
        row.Detail.Should().Be("shared@example.com");
    }
}
