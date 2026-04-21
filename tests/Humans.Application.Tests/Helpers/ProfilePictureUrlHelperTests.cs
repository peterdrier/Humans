using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Web.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Helpers;

/// <summary>
/// Covers <see cref="ProfilePictureUrlHelper"/> in isolation. Issue #532: the helper must
/// return custom-upload URLs or null, never Google avatar URLs.
/// </summary>
public class ProfilePictureUrlHelperTests
{
    [HumansFact]
    public async Task BuildEffectiveUrlsAsync_EmptyInput_ReturnsEmptyDictionary()
    {
        var profileService = Substitute.For<IProfileService>();
        var urlHelper = Substitute.For<IUrlHelper>();

        var result = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            profileService, urlHelper, []);

        result.Should().BeEmpty();

        // No repository call should have fired — the helper short-circuits on empty input.
        _ = profileService.DidNotReceiveWithAnyArgs().GetCustomPictureInfoByUserIdsAsync(
            default!, default);
    }

    [HumansFact]
    public async Task BuildEffectiveUrlsAsync_UserWithoutCustomPicture_ReturnsNull()
    {
        var userId = Guid.NewGuid();

        var profileService = Substitute.For<IProfileService>();
        profileService
            .GetCustomPictureInfoByUserIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>([]));

        var urlHelper = Substitute.For<IUrlHelper>();

        var result = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            profileService, urlHelper, [userId]);

        result.Should().ContainKey(userId);
        result[userId].Should().BeNull();
    }

    [HumansFact]
    public async Task BuildEffectiveUrlsAsync_UserWithCustomPicture_ReturnsCustomUrl()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        const long updatedAtTicks = 123456L;
        const string expectedUrl = "/Profile/Picture?id=profile-id&v=123456";

        var profileService = Substitute.For<IProfileService>();
        profileService
            .GetCustomPictureInfoByUserIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>(
                [(profileId, userId, updatedAtTicks)]));

        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.Action(Arg.Any<UrlActionContext>()).Returns(expectedUrl);

        var result = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            profileService, urlHelper, [userId]);

        result.Should().ContainKey(userId);
        result[userId].Should().Be(expectedUrl);
    }

    [HumansFact]
    public async Task BuildEffectiveUrlsAsync_MixedUsers_ReturnsUrlOrNullPerUser()
    {
        var userWithCustom = Guid.NewGuid();
        var userWithoutCustom = Guid.NewGuid();
        var profileId = Guid.NewGuid();

        var profileService = Substitute.For<IProfileService>();
        profileService
            .GetCustomPictureInfoByUserIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>(
                [(profileId, userWithCustom, 42L)]));

        var urlHelper = Substitute.For<IUrlHelper>();
        urlHelper.Action(Arg.Any<UrlActionContext>()).Returns("/custom-url");

        var result = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            profileService, urlHelper, [userWithCustom, userWithoutCustom]);

        result[userWithCustom].Should().Be("/custom-url");
        result[userWithoutCustom].Should().BeNull();
    }

    [HumansFact]
    public async Task BuildEffectiveUrlsAsync_DuplicateUserIds_AreDeduplicated()
    {
        var userId = Guid.NewGuid();

        var profileService = Substitute.For<IProfileService>();
        profileService
            .GetCustomPictureInfoByUserIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Guid ProfileId, Guid UserId, long UpdatedAtTicks)>>([]));

        var urlHelper = Substitute.For<IUrlHelper>();

        var result = await ProfilePictureUrlHelper.BuildEffectiveUrlsAsync(
            profileService, urlHelper, [userId, userId, userId]);

        result.Should().HaveCount(1);
        result[userId].Should().BeNull();
    }
}
