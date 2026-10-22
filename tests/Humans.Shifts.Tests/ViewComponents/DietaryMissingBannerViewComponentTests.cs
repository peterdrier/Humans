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

    private void StubDietary(Guid userId, string? dietary, bool declined = false) =>
        _cantina.GetDietaryProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(dietary is null && !declined
                ? null
                : new DietaryProfileInfo(userId, dietary, [], null, [], null, declined));

    [HumansTheory]
    [InlineData(false)]  // nothing on file
    [InlineData(true)]   // a row with no preference and no opt-out (e.g. allergies only)
    public async Task Renders_WhenQualifyingSignupAndNoPreferenceOrOptOut(bool rowWithoutPreference)
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, Arg.Any<CancellationToken>()).Returns(true);
        _cantina.GetDietaryProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(rowWithoutPreference ? new DietaryProfileInfo(userId, null, ["Egg"], null, [], null) : null);

        var result = await _sut.InvokeAsync(userId);

        result.Should().BeOfType<ViewViewComponentResult>();
    }

    [HumansTheory]
    [InlineData(true, "Vegan", false)]  // answered
    [InlineData(true, null, true)]      // opted out via "I don't want to answer"
    [InlineData(false, null, false)]    // no qualifying signup
    [InlineData(false, "Vegan", false)] // neither
    public async Task DoesNotRender_WhenGateMissed(bool hasQualifying, string? dietary, bool declined)
    {
        var userId = Guid.NewGuid();
        _shiftMgmt.HasQualifyingCantinaSignupAsync(userId, Arg.Any<CancellationToken>()).Returns(hasQualifying);
        StubDietary(userId, dietary, declined);

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
