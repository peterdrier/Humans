using Humans.Users.Contracts;

namespace Humans.Users.Models;

/// <summary>
/// Model for the <c>/Profile/Search</c> page. It sat in <c>TeamViewModels.cs</c> until
/// Teams' G5 and is bound only by <c>ProfileController</c>, so it stayed behind.
/// </summary>
/// <remarks>
/// Hits are carried unprojected: the view renders one
/// <c>&lt;vc:user-search-result&gt;</c> per row, so nothing here has to know what a
/// human looks like (nobodies-collective/Humans#1062).
/// </remarks>
internal sealed class HumanSearchViewModel
{
    public string? Query { get; set; }
    public List<HumanSearchResult> Results { get; set; } = [];
}
