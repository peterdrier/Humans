using AwesomeAssertions;
using Humans.MailerLite.Services.Dtos;
using NodaTime;

namespace Humans.MailerLite.Tests.Services;

public class MailerLiteImportServiceDriftTests
{
    [HumansTheory]
    [Xunit.InlineData(false)]
    [Xunit.InlineData(true)]
    public async Task ComputeDrift_CountsHumansOptedOutButMlActive(bool includeAlias)
    {
        var optedOutId = Guid.NewGuid();
        var optedInId = Guid.NewGuid();
        var harness = new ApplyHarness();

        static MailerLiteSubscriber Active(string id, string email) => new(
            id, email, "active", "api",
            Instant.FromUtc(2026, 1, 1, 0, 0), null, null, null, null, [ApplyHarness.WebsiteGroupId]);

        var subscribers = new List<MailerLiteSubscriber>
        {
            Active("ml-out", "out@x.com"), Active("ml-in", "in@x.com")
        };
        if (includeAlias)
        {
            subscribers.Add(Active("ml-alias", "alias@x.com"));
            harness.SetVerifiedMatch("alias@x.com", optedOutId);
        }
        harness.MlReturns(subscribers.ToArray());
        harness.SetVerifiedMatch("out@x.com", optedOutId);
        harness.SetVerifiedMatch("in@x.com", optedInId);
        harness.SetMarketingOptedOut(optedOutId, optedOut: true);
        harness.SetMarketingOptedOut(optedInId, optedOut: false);

        var drift = await harness.Service.ComputeDriftAsync(Xunit.TestContext.Current.CancellationToken);

        drift.HumansOptedOutMlActive.Should().Be(1);
        drift.HumansOptedInMlAbsent.Should().BeNull();
    }
}
