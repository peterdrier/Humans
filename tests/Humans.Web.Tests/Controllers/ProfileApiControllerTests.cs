using System.Security.Claims;
using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Controllers;
using NodaTime;
using Humans.Web.Models;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

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
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IContactFieldService _contactFieldService = Substitute.For<IContactFieldService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly UserManager<User> _userManager;

    public ProfileApiControllerTests()
    {
        var userStore = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            userStore, null, null, null, null, null, null, null, null);
    }

    private ProfileApiController BuildSut(User? currentUser)
    {
        if (currentUser is not null)
        {
            _userManager.GetUserAsync(Arg.Any<ClaimsPrincipal>()).Returns(currentUser);
            _userService.GetUserInfoAsync(currentUser.Id, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<UserInfo?>(MakeViewerUserInfo(currentUser)));
        }

        var ctrl = new ProfileApiController(
            _profileService, _userService, _contactFieldService, _userEmailService);

        var http = new DefaultHttpContext();
        if (currentUser is not null)
        {
            http.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, currentUser.Id.ToString())
                ],
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

    private static UserInfo MakeViewerUserInfo(User user) =>
        UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

    private static UserInfo MakeUserInfo(
        Guid userId,
        Guid profileId,
        string burnerName = "Target Burner",
        bool isRejected = false)
    {
        var profile = new Profile
        {
            Id = profileId,
            UserId = userId,
            BurnerName = burnerName,
            FirstName = "Target",
            LastName = "Display",
            IsApproved = true,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            RejectedAt = isRejected ? Instant.FromUtc(2026, 2, 1, 0, 0) : null,
        };
        var user = new User
        {
            Id = userId,
            DisplayName = "Target Display",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
        };
        return UserInfo.Create(
            user: user,
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
    }

    // ==========================================================================
    // Search — privacy gate behavior on the per-row detail line.
    // ==========================================================================

    [HumansFact]
    public async Task Search_returns_401_when_current_user_resolves_to_null()
    {
        // [Authorize] handles no-cookie at the framework layer. This covers the
        // session-valid-but-user-row-gone race: the action must fail closed
        // (401) instead of returning rows with empty details.
        var sut = BuildSut(currentUser: null);

        var result = await sut.Search(q: "David");

        result.Should().BeOfType<UnauthorizedResult>();

        await _userService.DidNotReceive().SearchUsersAsync(
            Arg.Any<string>(), Arg.Any<PersonSearchFields>(),
            Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _contactFieldService.DidNotReceive().GetViewerAccessLevelAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Search_surfaces_visible_primary_email_as_detail()
    {
        var viewer = MakeUser(Guid.NewGuid());
        var targetUserId = Guid.NewGuid();
        var targetProfileId = Guid.NewGuid();

        _userService.SearchUsersAsync(Arg.Any<string>(),
                Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([MakeSearchResult(targetUserId, targetProfileId, "David")]);

        _contactFieldService.GetViewerAccessLevelAsync(targetUserId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(ContactFieldVisibility.AllActiveProfiles);

        _userEmailService.GetVisibleEmailsAsync(targetUserId,
                ContactFieldVisibility.AllActiveProfiles, Arg.Any<CancellationToken>())
            .Returns([
                new UserEmailDto(Guid.NewGuid(), "alt@example.com",
                    IsVerified: true, IsGoogle: false, Provider: null, ProviderKey: null,
                    IsPrimary: false, Visibility: ContactFieldVisibility.AllActiveProfiles),
                new UserEmailDto(Guid.NewGuid(), "primary@example.com",
                    IsVerified: true, IsGoogle: false, Provider: null, ProviderKey: null,
                    IsPrimary: true, Visibility: ContactFieldVisibility.AllActiveProfiles)
            ]);

        var sut = BuildSut(viewer);

        var result = await sut.Search(q: "David");

        var row = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeAssignableTo<IEnumerable<HumanLookupSearchResult>>().Subject.Single();
        row.Detail.Should().Be("primary@example.com");

    }

    [HumansFact]
    public async Task Search_falls_back_to_contact_field_in_priority_order_when_no_visible_email()
    {
        var viewer = MakeUser(Guid.NewGuid());
        var targetUserId = Guid.NewGuid();
        var targetProfileId = Guid.NewGuid();

        _userService.SearchUsersAsync(Arg.Any<string>(),
                Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([MakeSearchResult(targetUserId, targetProfileId, "David")]);

        _contactFieldService.GetViewerAccessLevelAsync(targetUserId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(ContactFieldVisibility.AllActiveProfiles);
        _userEmailService.GetVisibleEmailsAsync(targetUserId, Arg.Any<ContactFieldVisibility>(), Arg.Any<CancellationToken>())
            .Returns([]);

        // Mix of types — Phone must win over Signal regardless of insert order.
        _contactFieldService.GetVisibleContactFieldsAsync(targetProfileId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns([
                new ContactFieldDto(Guid.NewGuid(), ContactFieldType.Signal, "Signal", "signal-handle",
                    ContactFieldVisibility.AllActiveProfiles),
                new ContactFieldDto(Guid.NewGuid(), ContactFieldType.Phone, "Phone", "+1-555-0100",
                    ContactFieldVisibility.AllActiveProfiles)
            ]);

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

        _userService.SearchUsersAsync(Arg.Any<string>(),
                Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([MakeSearchResult(targetUserId, targetProfileId, "David")]);

        _contactFieldService.GetViewerAccessLevelAsync(targetUserId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(ContactFieldVisibility.AllActiveProfiles);
        _userEmailService.GetVisibleEmailsAsync(targetUserId, Arg.Any<ContactFieldVisibility>(), Arg.Any<CancellationToken>())
            .Returns([]);
        _contactFieldService.GetVisibleContactFieldsAsync(targetProfileId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns([]);

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

        _userService.SearchUsersAsync(Arg.Any<string>(),
                Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([MakeSearchResult(targetUserId, targetProfileId, "David")]);

        _contactFieldService.GetViewerAccessLevelAsync(targetUserId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(ContactFieldVisibility.AllActiveProfiles);
        _userEmailService.GetVisibleEmailsAsync(targetUserId, Arg.Any<ContactFieldVisibility>(), Arg.Any<CancellationToken>())
            .Returns([]);

#pragma warning disable CS0618 // Verifying the controller skips the obsolete Email enum value.
        _contactFieldService.GetVisibleContactFieldsAsync(targetProfileId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns([
                new ContactFieldDto(Guid.NewGuid(), ContactFieldType.Email, "Email", "obsolete@example.com",
                    ContactFieldVisibility.AllActiveProfiles),
                new ContactFieldDto(Guid.NewGuid(), ContactFieldType.Discord, "Discord", "user#1234",
                    ContactFieldVisibility.AllActiveProfiles)
            ]);
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
        _userService.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));

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

        _userService.GetUserInfoAsync(targetUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(targetUserId, targetProfileId, isRejected: true)));

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

        _userService.GetUserInfoAsync(targetUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(MakeUserInfo(targetUserId, targetProfileId, burnerName: "Davey")));
        _contactFieldService.GetViewerAccessLevelAsync(targetUserId, viewer.Id, Arg.Any<CancellationToken>())
            .Returns(ContactFieldVisibility.AllActiveProfiles);
        _userEmailService.GetVisibleEmailsAsync(targetUserId, Arg.Any<ContactFieldVisibility>(), Arg.Any<CancellationToken>())
            .Returns([
                new UserEmailDto(Guid.NewGuid(), "shared@example.com",
                    IsVerified: true, IsGoogle: false, Provider: null, ProviderKey: null,
                    IsPrimary: true, Visibility: ContactFieldVisibility.AllActiveProfiles)
            ]);

        var sut = BuildSut(viewer);

        var result = await sut.GetByUserId(targetUserId);

        var row = result.Should().BeOfType<OkObjectResult>().Subject.Value
            .Should().BeOfType<HumanLookupSearchResult>().Subject;
        row.UserId.Should().Be(targetUserId);
        row.DisplayName.Should().Be("Davey");
        row.Detail.Should().Be("shared@example.com");
    }
}
