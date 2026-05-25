namespace Humans.Web.Models;

/// <summary>
/// Search scope for the inline person picker (<c>&lt;vc:human-search&gt;</c>).
/// Maps to the <c>scope</c> query parameter on <c>/api/profiles/search</c>.
/// </summary>
public enum HumanSearchScope
{
    /// <summary>Broad public match (bio / city / interests / CV). The default.</summary>
    All,

    /// <summary>Narrow matching to burner name only.</summary>
    Name,
}
