using AwesomeAssertions;
using Humans.Email.Services;
using Humans.Email.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NSubstitute;

namespace Humans.Email.Tests;

/// <summary>
/// The /Settings#email tab body (peterdrier/Humans#1634) — the same pause-state read
/// the /Email/EmailOutbox dashboard header used to show.
/// </summary>
public sealed class EmailPauseSettingsTabViewComponentTests
{
    private static async Task<EmailPauseSettingsTabViewModel> InvokeAsync(bool isPaused)
    {
        var outbox = Substitute.For<IEmailOutboxService>();
        outbox.IsEmailPausedAsync(Arg.Any<CancellationToken>()).Returns(isPaused);

        var sut = new EmailPauseSettingsTabViewComponent(outbox)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };

        var result = await sut.InvokeAsync();
        return (EmailPauseSettingsTabViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
    }

    [HumansFact]
    public async Task InvokeAsync_WhenPaused_ReturnsPausedModel()
    {
        var model = await InvokeAsync(isPaused: true);
        model.IsPaused.Should().BeTrue();
    }

    [HumansFact]
    public async Task InvokeAsync_WhenActive_ReturnsActiveModel()
    {
        var model = await InvokeAsync(isPaused: false);
        model.IsPaused.Should().BeFalse();
    }
}
