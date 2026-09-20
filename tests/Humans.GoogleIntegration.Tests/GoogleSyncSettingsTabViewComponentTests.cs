using AwesomeAssertions;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Models;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// The /Settings#google-sync tab body (peterdrier/Humans#1634) — the same mapping
/// GoogleController.SyncSettings used to build before that GET was removed.
/// </summary>
public sealed class GoogleSyncSettingsTabViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_MapsEachServiceWithItsFormattedNameAndUpdater()
    {
        var updaterId = Guid.NewGuid();
        var settings = Substitute.For<ISyncSettingsService>();
        settings.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<SyncServiceSettingsInfo>
        {
            new(Guid.NewGuid(), SyncServiceType.GoogleDrive, SyncMode.AddOnly,
                Instant.FromUtc(2026, 1, 1, 0, 0), updaterId),
        });

        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>())
            .Returns(new Dictionary<Guid, UserInfo> { [updaterId] = MakeUser(updaterId, "Roo") });

        var sut = new GoogleSyncSettingsTabViewComponent(settings, users);

        var result = await sut.InvokeAsync();
        var model = (SyncSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        var row = model.Settings.Should().ContainSingle().Which;
        row.ServiceType.Should().Be(SyncServiceType.GoogleDrive);
        row.ServiceName.Should().Be("Google Drive");
        row.CurrentMode.Should().Be(SyncMode.AddOnly);
        row.UpdatedByName.Should().Be("Roo");
    }

    private static UserInfo MakeUser(Guid userId, string burnerName) =>
        UserInfo.Create(
            new User { Id = userId, UserName = $"user-{userId:N}", BurnerName = burnerName },
            [], [], [], null, []);
}
