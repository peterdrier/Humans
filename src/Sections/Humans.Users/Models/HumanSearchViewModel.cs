using Humans.Users.Contracts;

namespace Humans.Users.Models;

/// <summary>
/// Model for the <c>/Profile/Search</c> page.
/// </summary>
/// <remarks>
/// Hits are carried unprojected — the view renders one
/// <c>&lt;vc:user-search-result&gt;</c> per row.
/// </remarks>
internal sealed class HumanSearchViewModel
{
    public string? Query { get; set; }
    public List<HumanSearchResult> Results { get; set; } = [];
}
