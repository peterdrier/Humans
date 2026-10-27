using System.Diagnostics.Metrics;
using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Consent.Contracts;
using Humans.Users.Contracts;
using Humans.Web.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Web.Tests.Services;

public class HumansMetricsServiceTests
{
    [HumansFact]
    public async Task StatusGauges_UseAccountStateRegardlessOfConsentReview()
    {
        var humans = Enum.GetValues<UserState>().Append(UserState.Active)
            .Select(state => UserInfo.Create(
                new User { Id = Guid.NewGuid(), Email = "human@example.com", State = state }, [], [], [],
                UserFixtures.Profile(burnerName: "Human", firstName: "First", lastName: "Last"), []))
            .ToList();
        var users = Substitute.For<IUserService>();
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(humans);
        var consent = Substitute.For<IConsentService>();
        consent.GetConsentReviewsAsync(Arg.Any<CancellationToken>()).Returns(
            humans.ToDictionary(u => u.Id, u => ConsentReviewInfo.None(u.Id) with
            {
                IsApproved = u.Id != humans[^1].Id,
            }));
        using var provider = new ServiceCollection()
            .AddSingleton(users).AddSingleton(consent).BuildServiceProvider();
        var totals = new Dictionary<string, int>(StringComparer.Ordinal);
        var pendingVolunteers = -1;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, observer) =>
        {
            if (instrument.Meter.Name is "Humans.Metrics"
                && instrument.Name is "humans.humans_total" or "humans.pending_volunteers")
                observer.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            if (instrument.Name is "humans.pending_volunteers")
                pendingVolunteers = value;
            else
                foreach (var tag in tags)
                    if (tag.Key is "status") totals[(string)tag.Value!] = value;
        });
        listener.Start();
        using var service = new HumansMetricsService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<HumansMetricsService>.Instance, Substitute.For<IUserActivityTracker>());

        await (Task)typeof(HumansMetricsService)
            .GetMethod("RefreshAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, null)!;
        listener.RecordObservableInstruments();

        totals.Should().BeEquivalentTo(new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["active"] = 2,
            ["pending"] = 1,
            ["suspended"] = 2,
            ["inactive"] = 4,
        });
        pendingVolunteers.Should().Be(1);
    }
}
