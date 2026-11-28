using AwesomeAssertions;
using Humans.Base.Interfaces;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;
using Humans.Camps.ViewComponents;

namespace Humans.Camps.Tests.Contributions;

public sealed class SearchContributorTests
{
    [HumansFact]
    public async Task Category_Search_IsUnboundedWithKeysPassed()
    {
        var id = Guid.NewGuid();
        var service = Substitute.For<ICampService>();
        service.SearchAsync("Kitchen", int.MaxValue, Arg.Any<CancellationToken>()).Returns(Task.FromResult<IReadOnlyList<CampSearchHit>>([new CampSearchHit(id, "Kitchen", 80)]));
        var localizer = Substitute.For<IStringLocalizer<CampsResource>>();
        localizer["Camps_Search_Category"].Returns(new LocalizedString("Camps_Search_Category", "Camps"));
        var services = new ServiceCollection().AddSingleton(service).AddSingleton(localizer).BuildServiceProvider();

        var category = ((ISearchContributor)new Section()).Categories().Should().ContainSingle().Subject;
        var hits = await category.SearchAsync(services, "Kitchen", TestContext.Current.CancellationToken);

        category.Label(services).Should().Be("Camps");
        category.ResultComponent.Should().Be<CampsSearchResultViewComponent>();
        hits.Should().ContainSingle().Which.Should().Be(new SearchHit(id, "Kitchen", 80));
    }
}
