using System.Security.Claims;
using AwesomeAssertions;
using Humans.Settings.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Tickets.Tests.ViewComponents;

/// <summary>The member dashboard's ticket card renders nothing off season, and an unavailable state when holdings fail to load.</summary>
public sealed class MemberTicketStatusViewComponentTests
{
    private readonly ITicketService _tickets = Substitute.For<ITicketService>();
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    private static EventSettingsInfo Event(bool isOffSeason) => new(
        Guid.NewGuid(), "Test Event", 2026, "UTC", new LocalDate(2026, 7, 1),
        -14, 6, 9, -14, -10, -7, -3, new Dictionary<int, int>(), null, null,
        IsOffSeason: isOffSeason);

    private MemberTicketStatusViewComponent BuildSut(TicketVendorSettings? vendor = null)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString())], "test"));
        return new MemberTicketStatusViewComponent(
            _tickets, _settings, Options.Create(vendor ?? new TicketVendorSettings()),
            NullLogger<MemberTicketStatusViewComponent>.Instance)
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = httpContext },
            },
        };
    }

    [HumansFact]
    public async Task InvokeAsync_OffSeason_RendersNothing()
    {
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(Event(isOffSeason: true));

        var result = await BuildSut().InvokeAsync();

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
    }

    [HumansFact]
    public async Task InvokeAsync_InSeason_RendersTheCardWithTheEventYear()
    {
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(Event(isOffSeason: false));

        var result = await BuildSut().InvokeAsync();

        var model = (MemberTicketStatusViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
        model.EventYear.Should().Be(2026);
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InvokeAsync_HoldingsReadFails_ShowsUnavailableNotMissing(bool fails)
    {
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(Event(isOffSeason: false));
        if (fails)
            _tickets.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns<UserTicketHoldings>(_ => throw new InvalidOperationException("vendor down"));
        else
            _tickets.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
                .Returns(new UserTicketHoldings(0, []));

        var result = await BuildSut(new TicketVendorSettings { EventId = "ev", ApiKey = "key" }).InvokeAsync();

        var model = (MemberTicketStatusViewModel)((ViewViewComponentResult)result).ViewData!.Model!;
        model.HoldingsUnavailable.Should().Be(fails);
        model.HasTicket.Should().BeFalse();
    }
}
