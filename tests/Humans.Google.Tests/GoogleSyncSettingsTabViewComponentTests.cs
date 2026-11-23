using Humans.Google.Domain;
using AwesomeAssertions;
using Humans.Google.Models;
using Humans.Google.Services;
using Humans.Google.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NSubstitute;

namespace Humans.Google.Tests;

public sealed class GoogleSyncSettingsTabViewComponentTests
{
    [HumansFact]
    public async Task InvokeAsync_RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var settings = Substitute.For<ISyncSettingsService>();
        settings.GetAllAsync(aborted.Token)
            .Returns(Task.FromException<IReadOnlyList<SyncServiceSettingsInfo>>(
                new OperationCanceledException(aborted.Token)));
        var sut = new GoogleSyncSettingsTabViewComponent(settings)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
                }
            }
        };

        var act = () => sut.InvokeAsync();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

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

        var sut = new GoogleSyncSettingsTabViewComponent(settings);

        var result = await sut.InvokeAsync();
        var model = (SyncSettingsViewModel)((ViewViewComponentResult)result).ViewData!.Model!;

        var row = model.Settings.Should().ContainSingle().Which;
        row.ServiceType.Should().Be(SyncServiceType.GoogleDrive);
        row.ServiceName.Should().Be("Google Drive");
        row.CurrentMode.Should().Be(SyncMode.AddOnly);
        row.UpdatedByUserId.Should().Be(updaterId);
    }
}
