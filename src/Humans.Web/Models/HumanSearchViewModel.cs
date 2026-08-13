using Humans.UI.Models;

namespace Humans.Web.Models;

/// <summary>
/// Model for Shell's <c>/Profile/Search</c> page. It sat in <c>TeamViewModels.cs</c> until
/// Teams' G5 and is bound only by <c>ProfileController</c>, so it stayed behind.
/// </summary>
/// <remarks>
/// <c>HumanSearchResultViewModel</c> is in <c>Humans.UI/Models</c>: the Search section's
/// <c>/Search</c> page binds it too and a section cannot name a <c>Humans.Web</c> type
/// (G5-SECTION-TEMPLATE.md step 6).
/// </remarks>
public class HumanSearchViewModel
{
    public string? Query { get; set; }
    public List<HumanSearchResultViewModel> Results { get; set; } = [];
}
