using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Tickets;
using Humans.Domain.Enums;
using Humans.Web.Models;
using Humans.Web.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.ViewComponents;

public class CommunicationPreferencesPanelViewComponentTests
{
    [HumansFact]
    public async Task MissingMarketingRow_RendersUnchecked()
    {
        // Regression: a deleted/never-set Marketing pref (null) must render as
        // opted-out — matching what the send path treats null as — not checked.
        var model = await InvokeWith(prefs: []);

        Item(model, MessageCategory.Marketing).EmailEnabled.Should().BeFalse();
    }

    [HumansFact]
    public async Task MissingNonMarketingRow_RendersChecked()
    {
        // Every other category is opt-in-by-default, so a missing row stays checked.
        var model = await InvokeWith(prefs: []);

        Item(model, MessageCategory.Governance).EmailEnabled.Should().BeTrue();
    }

    [HumansFact]
    public async Task ExistingMarketingOptIn_RendersChecked()
    {
        var model = await InvokeWith(prefs:
        [
            new CommunicationPreferenceSnapshot(
                MessageCategory.Marketing, OptedOut: false, InboxEnabled: true,
                "Profile", Instant.FromUtc(2026, 1, 1, 0, 0)),
        ]);

        Item(model, MessageCategory.Marketing).EmailEnabled.Should().BeTrue();
    }

    [HumansFact]
    public async Task ExistingMarketingOptOut_RendersUnchecked()
    {
        var model = await InvokeWith(prefs:
        [
            new CommunicationPreferenceSnapshot(
                MessageCategory.Marketing, OptedOut: true, InboxEnabled: true,
                "Profile", Instant.FromUtc(2026, 1, 1, 0, 0)),
        ]);

        Item(model, MessageCategory.Marketing).EmailEnabled.Should().BeFalse();
    }

    private static CategoryPreferenceItem Item(CommunicationPreferencesViewModel model, MessageCategory category) =>
        model.Categories.Single(c => c.Category == category);

    private static async Task<CommunicationPreferencesViewModel> InvokeWith(
        IReadOnlyList<CommunicationPreferenceSnapshot> prefs)
    {
        var commPrefs = Substitute.For<ICommunicationPreferenceService>();
        commPrefs.GetPreferencesReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(prefs);

        var tickets = Substitute.For<ITicketService>();
        tickets.GetUserTicketHoldingsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new UserTicketHoldings(0, []));

        var sut = new CommunicationPreferencesPanelViewComponent(
            commPrefs, tickets, new FakeClock(Instant.FromUtc(2026, 5, 20, 0, 0)))
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };

        var result = await sut.InvokeAsync(Guid.NewGuid()) as ViewViewComponentResult;
        return (CommunicationPreferencesViewModel)result!.ViewData!.Model!;
    }
}
