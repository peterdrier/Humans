using Humans.Google.Domain;
using AwesomeAssertions;
using Humans.Google.Models;
using Humans.Google.Services;
using Humans.Google.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Google.Tests;

public sealed class GoogleSyncSettingsTabViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_MapsEachServiceWithItsFormattedNameAndUpdater()
    {
        var updaterId = Guid.NewGuid();
        var settings = Substitute.For<ISyncSettingsService>();
        settings.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<SyncServiceSettingsInfo>
        {
            new(SyncServiceType.GoogleDrive, SyncMode.AddOnly,
                Instant.FromUtc(2026, 1, 1, 0, 0), updaterId),
        });

        var sut = new GoogleSyncSettingsTabViewComponent(settings);

        var result = await sut.InvokeAsync();
        var model = (IReadOnlyList<SyncServiceSettingViewModel>)((ViewViewComponentResult)result).ViewData!.Model!;

        var row = model.Should().ContainSingle().Which;
        row.ServiceType.Should().Be(SyncServiceType.GoogleDrive);
        row.ServiceName.Should().Be("Google Drive");
        row.CurrentMode.Should().Be(SyncMode.AddOnly);
        row.UpdatedByUserId.Should().Be(updaterId);
    }
}
