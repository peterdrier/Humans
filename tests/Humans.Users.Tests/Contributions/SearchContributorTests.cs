using Humans.Testing;
using AwesomeAssertions;
using Humans.Base.Interfaces;
using Humans.Users.Contracts;
using Humans.Users.ViewComponents;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using NSubstitute;
using Xunit;

namespace Humans.Users.Tests.Contributions;

/// <summary>
/// Pinned here and nowhere else: the Humans category's field mask (<see cref="PersonSearchFields.PublicAll"/>,
/// the global search's only search-time privacy filter) on text and GUID queries alike.
/// </summary>
public sealed class SearchContributorTests
{
    private readonly IUserService _users = Substitute.For<IUserService>();

    [HumansTheory]
    [InlineData("Kitchen")]
    [InlineData("4b0b5f8e-61a4-4c7e-9e5f-2f2a1c1d0b11")]
    public async Task Category_AsksForPublicFieldsOnly_WithAnUnboundedCap(string query)
    {
        await Category().SearchAsync(Services(), query, TestContext.Current.CancellationToken);

        await _users.Received(1).SearchUsersAsync(
            query, PersonSearchFields.PublicAll, int.MaxValue, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Category_MatchContext_SortsByBurnerName()
    {
        var human = new HumanSearchResult(
            Guid.NewGuid(), "Sparkle", null, "Bio", "…builds <em>kitchens</em>…", null, 40);
        _users.SearchUsersAsync(Arg.Any<string>(), Arg.Any<PersonSearchFields>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<HumanSearchResult>>([human]));
        var services = Services();

        var category = Category();
        var hits = await category.SearchAsync(services, "kitchen", TestContext.Current.CancellationToken);

        category.Label(services).Should().Be("Humans");
        category.ResultComponent.Should().Be<UserSearchResultViewComponent>();
        hits.Should().ContainSingle().Which.Should().Be(
            new SearchHit(human.UserId, "Sparkle", 40, "Bio", "…builds <em>kitchens</em>…"));
    }

    private static SearchCategory Category() =>
        ((ISearchContributor)new Section()).Categories().Should().ContainSingle().Subject;

    private ServiceProvider Services()
    {
        var localizer = Substitute.For<IStringLocalizer<UsersResource>>().AddFormatting();
        localizer["Users_Search_Category"].Returns(new LocalizedString("Users_Search_Category", "Humans"));
        return new ServiceCollection().AddSingleton(_users).AddSingleton(localizer).BuildServiceProvider();
    }
}
