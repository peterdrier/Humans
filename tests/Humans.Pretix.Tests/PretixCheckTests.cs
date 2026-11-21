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

    [HumansFact]
    public async Task A_ready_event_passes_with_reads_only()
    {
        var pretix = Organizer()
            .On("GET", Api, HttpStatusCode.OK, new { currency = "EUR", live = true, testmode = false })
            .On("GET", Api + "items/", HttpStatusCode.OK, new
            {
                results = new[] { new { id = 1, internal_name = "humans-ticket" }, new { id = 2, internal_name = "humans-donation" } },
            });

        var findings = await Create(pretix).CheckAsync("nowhere2027", Ct);

        findings.Should().OnlyContain(f => f.Ok);
        pretix.Requests.Should().OnlyContain(r => r.Route.StartsWith("GET ", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task An_offline_event_missing_a_carrier_says_both()
    {
        var pretix = Organizer()
            .On("GET", Api, HttpStatusCode.OK, new { currency = "EUR", live = false, testmode = true })
            .On("GET", Api + "items/", HttpStatusCode.OK, new { results = new[] { new { id = 1, internal_name = "humans-ticket" } } });

        var findings = await Create(pretix).CheckAsync("nowhere2027", Ct);

        findings.Where(f => !f.Ok).Select(f => f.Text).Should().HaveCount(2)
            .And.Contain(t => t.Contains("offline"))
            .And.Contain(t => t.Contains("humans-donation"));
    }

    [HumansFact]
    public async Task A_rejected_token_stops_before_the_event()
    {
        var pretix = new FakePretix()
            .On("GET", "/api/v1/organizers/nobodies/events/", HttpStatusCode.Unauthorized, new { detail = "Invalid token." });

        var findings = await Create(pretix).CheckAsync("nowhere2027", Ct);

        findings.Should().Contain(f => !f.Ok && f.Text.Contains("401"));
        pretix.Requests.Should().ContainSingle();
    }

    [HumansFact]
    public void Setup_labels_pretix_hosted_and_hides_secrets()
    {
        var settings = PretixTestHost.Settings();
        settings.BaseUrl = "https://pretix.eu/";

        var setup = Create(new FakePretix(), settings).Setup;

        setup.Hosting.Should().Be("pretix Hosted (pretix.eu)");
        setup.AdminUrl.Should().Be(new Uri("https://pretix.eu/control/organizer/nobodies/"));
        setup.Settings.Single(s => string.Equals(s.Name, "Pretix:Organizer", StringComparison.Ordinal)).Value.Should().Be("nobodies");
        setup.Settings.Where(s => s.IsSecret).Should().OnlyContain(s => s.IsSet && s.Value == null);
    }
}
