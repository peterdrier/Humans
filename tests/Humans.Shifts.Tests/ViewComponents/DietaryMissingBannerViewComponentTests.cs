using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Humans.Shifts.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Humans.Cantina.Contracts;

namespace Humans.Shifts.Tests.ViewComponents;

/// <summary>
/// Covers the visibility gate inside <see cref="DietaryMissingBannerViewComponent"/>.
/// Spec: src/Sections/Humans.Cantina/Docs/features/dietary-nudge.md (US-35.6)
/// </summary>
public class DietaryMissingBannerViewComponentTests
{
    private readonly IShiftService _shiftMgmt = Substitute.For<IShiftService>();
    private readonly ICantinaService _cantina = Substitute.For<ICantinaService>();
    private readonly DietaryMissingBannerViewComponent _sut;

    public DietaryMissingBannerViewComponentTests()
    {
        _sut = new DietaryMissingBannerViewComponent(
            _shiftMgmt,
            _cantina,
            NullLogger<DietaryMissingBannerViewComponent>.Instance);
        _sut.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() }
        };
    }

    private void StubDietary(Guid userId, string? dietary) =>
        _cantina.GetDietaryProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(dietary is null ? null : new DietaryProfileInfo(userId, dietary, [], null, [], null));

    [HumansFact]
    public async Task Renders_WhenQualifyingSignupAndDietaryEmpty()
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, Arg.Any<CancellationToken>()).Returns(true);
        StubDietary(userId, null);

        var result = await _sut.InvokeAsync(userId);

        result.Should().BeOfType<ViewViewComponentResult>();
    }

    [HumansTheory]
    [InlineData(true, "Vegan")]   // dietary filled
    [InlineData(false, null)]      // no qualifying signup
    [InlineData(false, "Vegan")]   // neither
    public async Task DoesNotRender_WhenGateMissed(bool hasQualifying, string? dietary)
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, Arg.Any<CancellationToken>()).Returns(hasQualifying);
        StubDietary(userId, dietary);

        var result = await _sut.InvokeAsync(userId);

        result.Should().BeOfType<ContentViewComponentResult>()
              .Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DoesNotRender_WhenServiceThrows()
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, Arg.Any<CancellationToken>())
                  .Returns<Task<bool>>(_ => throw new InvalidOperationException("transient DB error"));

        var result = await _sut.InvokeAsync(userId);

        result.Should().BeOfType<ContentViewComponentResult>()
              .Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        _sut.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = new ViewContext
            {
                HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
            }
        };
        _shiftMgmt.HasQualifyingCantinaSignupAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(Task.FromException<bool>(new OperationCanceledException(aborted.Token)));

        var act = () => _sut.InvokeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
