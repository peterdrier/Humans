using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Email.Models;
using Humans.Email.ViewComponents;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Email.Tests.ViewComponents;

public class CommunicationPreferencesPanelViewComponentTests
{
    [HumansFact]
    public async Task RequestAborted_PropagatesCancellation()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var commPrefs = Substitute.For<ICommunicationPreferenceService>();
        commPrefs.GetPreferencesReadOnlyAsync(Arg.Any<Guid>(), aborted.Token)
            .Returns(Task.FromException<IReadOnlyList<CommunicationPreferenceSnapshot>>(
                new OperationCanceledException(aborted.Token)));
        var sut = new CommunicationPreferencesPanelViewComponent(
            commPrefs, [Substitute.For<ICommunicationPreferenceLock>()], new FakeClock(Instant.FromUtc(2026, 5, 20, 0, 0)))
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext
                {
                    HttpContext = new DefaultHttpContext { RequestAborted = aborted.Token }
                }
            }
        };

        var act = () => sut.InvokeAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansFact]
    public async Task MissingMarketingRow_RendersUnchecked()
    {
        // Regression: a deleted/never-set Marketing pref (null) must render as
        // opted-out — matching what the send path treats null as — not checked.
        var model = await InvokeWithAsync(prefs: []);

        Item(model, MessageCategory.Marketing).EmailEnabled.Should().BeFalse();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingNonMarketingRow_RendersCheckedWithLock(bool locked)
    {
        // Every other category is opt-in-by-default, so a missing row stays checked.
        var model = await InvokeWithAsync(prefs: [], locked);

        Item(model, MessageCategory.Governance).EmailEnabled.Should().BeTrue();
        var ticketing = Item(model, MessageCategory.Ticketing);
        ticketing.EmailEnabled.Should().BeTrue();
        ticketing.AlertEnabled.Should().BeTrue();
        ticketing.IsLocked.Should().Be(locked);
        ticketing.EmailEditable.Should().Be(!locked);
        ticketing.AlertEditable.Should().Be(!locked);
    }

    [HumansFact]
    public async Task ExistingMarketingOptIn_RendersChecked()
    {
        var model = await InvokeWithAsync(prefs:
        [
            new CommunicationPreferenceSnapshot(
                MessageCategory.Marketing, OptedOut: false, InboxEnabled: true,
                "Profile", Instant.FromUtc(2026, 1, 1, 0, 0)),
        ]);

        var marketing = Item(model, MessageCategory.Marketing);
        marketing.EmailEnabled.Should().BeTrue();
        marketing.UpdateSource.Should().Be("Profile");
        marketing.UpdatedAt.Should().Be(Instant.FromUtc(2026, 1, 1, 0, 0));
    }

    [HumansFact]
    public async Task ExistingMarketingOptOut_RendersUnchecked()
    {
        var model = await InvokeWithAsync(prefs:
        [
            new CommunicationPreferenceSnapshot(
                MessageCategory.Marketing, OptedOut: true, InboxEnabled: true,
                "Profile", Instant.FromUtc(2026, 1, 1, 0, 0)),
        ]);

        Item(model, MessageCategory.Marketing).EmailEnabled.Should().BeFalse();
    }

    private static CategoryPreferenceItem Item(CommunicationPreferencesViewModel model, MessageCategory category) =>
        model.Categories.Single(c => c.Category == category);

    private static async Task<CommunicationPreferencesViewModel> InvokeWithAsync(
        IReadOnlyList<CommunicationPreferenceSnapshot> prefs, bool ticketingLocked = false)
    {
        var commPrefs = Substitute.For<ICommunicationPreferenceService>();
        commPrefs.GetPreferencesReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(prefs);

        var preferenceLock = Substitute.For<ICommunicationPreferenceLock>();
        preferenceLock.GetLockedCategoriesAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<MessageCategory>>(
                ticketingLocked ? [MessageCategory.Ticketing] : []));

        var sut = new CommunicationPreferencesPanelViewComponent(
            commPrefs, [preferenceLock], new FakeClock(Instant.FromUtc(2026, 5, 20, 0, 0)))
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
