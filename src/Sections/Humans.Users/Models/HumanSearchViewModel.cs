using Humans.Users.Contracts;

namespace Humans.Users.Models;

/// <summary>Hits are carried unprojected; the view renders one <c>&lt;vc:user-search-result&gt;</c> per row.</summary>
internal sealed class HumanSearchViewModel
{
    public string? Query { get; set; }
    public List<HumanSearchResult> Results { get; set; } = [];
}
