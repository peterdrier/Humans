using Humans.Testing;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;
using Humans.Shifts.ViewComponents;

namespace Humans.Shifts.Tests.Contributions;

public sealed class SearchContributorTests
{
    [HumansFact]
    public async Task Category_Search_PassesKeyWithScoreThrough()
    {
        var id = Guid.NewGuid();
        var service = Substitute.For<IShiftService>();
        service.SearchAsync("Kitchen", int.MaxValue, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<RotaSearchHit>>([new RotaSearchHit(id, "Kitchen", 80)]));
        var localizer = Substitute.For<IStringLocalizer<ShiftsResource>>().AddFormatting();
        localizer["Shifts_Search_Category"].Returns(new LocalizedString("Shifts_Search_Category", "Shifts"));
        var services = new ServiceCollection().AddSingleton(service).AddSingleton(localizer).BuildServiceProvider();

        var category = ((ISearchContributor)new Section()).Categories().Should().ContainSingle().Subject;
        var hits = await category.SearchAsync(services, "Kitchen", TestContext.Current.CancellationToken);

        category.Label(services).Should().Be("Shifts");
        category.ResultComponent.Should().Be<ShiftsSearchResultViewComponent>();
        hits.Should().ContainSingle().Which.Should().Be(new SearchHit(id, "Kitchen", 80));
    }
}
