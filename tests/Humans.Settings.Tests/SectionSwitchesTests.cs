using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Settings.Contracts;
using Humans.Settings.Models;
using Humans.Settings.Services;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using TestContext = Xunit.TestContext;

namespace Humans.Settings.Tests;

public sealed class SectionSwitchesTests
{
    private readonly ISettingsService _settings = Substitute.For<ISettingsService>();

    private SectionSwitches BuildSut()
    {
        var services = new ServiceCollection();
        services.AddScoped(_ => _settings);
        return new SectionSwitches(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SectionSwitches>.Instance);
    }

    /// <summary>Sign-in and the error pages ask too, and must render with the database down.</summary>
    [HumansFact]
    public async Task AFailedRead_SwitchesNothingOff_AndIsNotCached()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns<EventSettingsInfo?>(_ => throw new IOException("database down"), _ => Event("Cantina"));
        var sut = BuildSut();

        (await sut.GetSwitchedOffAsync(ct)).Should().BeEmpty();
        (await sut.GetSwitchedOffAsync(ct)).Should().BeEquivalentTo(["Cantina"]);
    }

    private static EventSettingsInfo Event(params string[] switchedOff) => new(
        Id: Guid.NewGuid(),
        EventName: "Kiez Burn 2027",
        Year: 2027,
        TimeZoneId: "Europe/Berlin",
        GateOpeningDate: new LocalDate(2027, 6, 22),
        BuildStartOffset: -5,
        EventEndOffset: 6,
        StrikeEndOffset: 8,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        SwitchedOffSections: switchedOff);

    [HumansFact]
    public async Task NoActiveEvent_SwitchesNothingOff()
    {
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns((EventSettingsInfo?)null);

        (await BuildSut().GetSwitchedOffAsync(TestContext.Current.CancellationToken)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task TheActiveEventsList_IsReadOnceAndMatchedIgnoringCase()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(Event("Cantina", "Rideshare"));
        var sut = BuildSut();

        var first = await sut.GetSwitchedOffAsync(ct);
        await sut.GetSwitchedOffAsync(ct);

        first.Should().BeEquivalentTo(["Cantina", "Rideshare"]);
        first.Contains("cantina").Should().BeTrue();
        await _settings.Received(1).GetActiveEventSettingsAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>The flush is what makes a saved form, or a different event going active, take effect.</summary>
    [HumansFact]
    public async Task AnEventSettingsWrite_FlushesTheList()
    {
        var ct = TestContext.Current.CancellationToken;
        _settings.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(Event("Cantina"), Event());
        var sut = BuildSut();
        (await sut.GetSwitchedOffAsync(ct)).Should().BeEquivalentTo(["Cantina"]);

        sut.EventSettingsChanged(Guid.NewGuid());

        (await sut.GetSwitchedOffAsync(ct)).Should().BeEmpty();
    }

    [HumansFact]
    public void TheForm_RoundTripsTheListSortedAndDeduplicated()
    {
        var model = EventSettingsFormMapper.ToViewModel(Event("Rideshare", "Cantina"));
        model.SwitchedOffSections.Add("Cantina");

        var parsed = EventSettingsFormMapper.Parse(model);

        parsed.Success.Should().BeTrue();
        parsed.Settings!.SwitchedOffSections.Should().Equal("Cantina", "Rideshare");
    }

    [HumansFact]
    public void TheNextEvent_StartsWithThePreviousEventsList()
    {
        EventSettingsFormMapper.NextEvent(Event("Cantina")).SwitchedOffSections.Should().Equal("Cantina");
    }
}
