using System.Net;
using AwesomeAssertions;
using Xunit;
using static Humans.Pretix.Tests.PretixTestHost;

namespace Humans.Pretix.Tests;

public class PretixCheckTests
{
    private static readonly CancellationToken Ct = TestContext.Current.CancellationToken;

    private static FakePretix Organizer() => new FakePretix()
        .On("GET", "/api/v1/organizers/nobodies/events/", HttpStatusCode.OK, new { results = Array.Empty<object>() });

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Check_CallerCancels_Propagates(bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        if (cancelCaller) await caller.CancelAsync();
        var pretix = new FakePretix { Failure = new TaskCanceledException("probe cancelled", null, caller.Token) };
        var check = () => Create(pretix).CheckAsync(null, caller.Token);

        if (cancelCaller)
            await check.Should().ThrowAsync<OperationCanceledException>();
        else
            (await check()).Should().Contain(f => !f.Ok && f.Text.Contains("could not be read"));
    }

    [HumansFact]
    public async Task Check_ReadyEvent_Passes()
    {
        var pretix = Organizer()
            .On("GET", Api, HttpStatusCode.OK, new { currency = "EUR", live = true, testmode = false })
            .On("GET", Api + "items/", HttpStatusCode.OK, new
            {
                results = new[] { new { id = 1, internal_name = "humans-ticket" } },
                next = $"https://pretix.test{Api}items/?page=2",
            })
            .On("GET", Api + "items/", HttpStatusCode.OK, new
            {
                results = new[] { new { id = 2, internal_name = "humans-donation" } },
            });

        var findings = await Create(pretix).CheckAsync("nowhere2027", Ct);

        findings.Should().OnlyContain(f => f.Ok);
        pretix.Requests.Should().OnlyContain(r => r.Route.StartsWith("GET ", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Check_OfflineEventNoCarrier_SaysBoth()
    {
        var pretix = Organizer()
            .On("GET", Api, HttpStatusCode.OK, new { currency = "EUR", live = false, testmode = true })
            .On("GET", Api + "items/", HttpStatusCode.OK, new { results = new[] { new { id = 1, internal_name = "humans-ticket" } } });

        var findings = await Create(pretix).CheckAsync("nowhere2027", Ct);

        findings.Where(f => !f.Ok).Select(f => f.Text).Should().HaveCount(3)
            .And.Contain(t => t.Contains("offline"))
            .And.Contain(t => t.Contains("test mode"))
            .And.Contain(t => t.Contains("humans-donation"));
    }

    [HumansFact]
    public async Task Check_RejectedToken_StopsBeforeEvent()
    {
        var pretix = new FakePretix()
            .On("GET", "/api/v1/organizers/nobodies/events/", HttpStatusCode.Unauthorized, new { detail = "Invalid token." });

        var findings = await Create(pretix).CheckAsync("nowhere2027", Ct);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains("401"));
        pretix.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public async Task Check_ItemsReadRefused_IsFinding()
    {
        var pretix = Organizer()
            .On("GET", Api, HttpStatusCode.OK, new { currency = "EUR", live = true, testmode = false })
            .On("GET", Api + "items/", HttpStatusCode.Forbidden, new { detail = "No permission." });

        var findings = await Create(pretix).CheckAsync("nowhere2027", Ct);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains("403"));
    }

    [HumansFact]
    public async Task Check_PathLikeEventId_IsRefused()
    {
        var pretix = Organizer();

        var findings = await Create(pretix).CheckAsync("..", Ct);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains("not a pretix event slug"));
        pretix.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public void Setup_HidesSecrets()
    {
        var settings = PretixTestHost.Settings();
        settings.BaseUrl = "https://pretix.eu/";

        var setup = Create(new FakePretix(), settings).Setup;

        setup.Hosting.Should().Be("pretix Hosted (pretix.eu)");
        setup.Notes.Should().Contain(n => n.Contains("by hand"));
        setup.AdminUrl.Should().Be(new Uri("https://pretix.eu/control/organizer/nobodies/"));
        setup.Settings.Single(s => string.Equals(s.Name, "Pretix:Organizer", StringComparison.Ordinal)).Value.Should().Be("nobodies");
        setup.Settings.Where(s => s.IsSecret).Should().OnlyContain(s => s.IsSet && s.Value == null);
    }
}
